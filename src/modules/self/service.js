'use strict';

/**
 * 「数字人分身」服务层：把 selfModel / access 的纯数据能力包成 HTTP 用例。
 *
 * 这里不创建路由、不启动任务；manifest 只声明，routes 按需取值。
 * 所有接口只允许本机回环访问（在 routes 里拦），返回内容属于主人数据，
 * 不进日志正文，只记条数 / id / 大小。
 */
const selfModel = require('../../persona/selfModel');
const access = require('../../security/access');
const time = require('../../infra/time');
const { deepMerge } = require('../../infra/config');
const configPatch = require('../../infra/configPatch');
const { describeError } = require('../../infra/errors');

function fail(message, statusCode = 400) {
  return Object.assign(new Error(message), { statusCode });
}

// 提醒状态 → 待确认队列的发送回执状态。读不到或还在队列里时保持 queued。
function receiptState(status) {
  switch (String(status || '')) {
    case 'done':
    case 'sent':
      return 'sent';
    case 'unconfirmed':
    case 'uncertain':
      return 'uncertain';
    case 'failed':
    case 'expired':
    case 'cancelled':
      return 'failed';
    default:
      return '';
  }
}

const ON_DUTY_ITEM_STATES = new Set(['pending', 'queued', 'sent', 'failed', 'uncertain']);

class SelfService {
  constructor(config, { logger = null, runtime = null } = {}) {
    this.config = config;
    this.logger = logger;
    this.runtime = runtime;
  }

  /* ------------------------------ 自我模型 ------------------------------ */

  modelSnapshot() {
    selfModel.ensureDefaultFiles(this.config);
    const raw = selfModel.readRaw(this.config);
    const checked = selfModel.validate(raw && typeof raw === 'object' ? raw : {});
    return {
      model: checked.model,
      // 启动器同时接受 selfModel 别名，避免字段名歧义。
      selfModel: checked.model,
      warnings: checked.warnings,
      scopes: access.SCOPES.slice(),
      updatedAt: checked.model.updatedAt,
    };
  }

  saveModel(body = {}) {
    const current = selfModel.load(this.config);
    let next;
    if (body && body.model && typeof body.model === 'object') next = body.model;
    else if (body && body.patch && typeof body.patch === 'object') next = deepMerge(current, body.patch);
    else next = deepMerge(current, body || {});
    const saved = selfModel.save(this.config, next);
    this.logger?.info?.(`[self] model saved knowledge=${saved.model.knowledge.length} stances=${saved.model.stances.length} templates=${saved.model.templates.length}`);
    return { model: saved.model, warnings: saved.warnings };
  }

  /* ------------------------------ 联系人权限 ------------------------------ */

  policySnapshot() {
    const doc = access.loadDoc(this.config);
    const contacts = Object.keys(doc.contacts)
      .map((uid) => {
        const policy = access.policyFor(doc, uid);
        return {
          uid: policy.uid,
          name: policy.name,
          scopes: policy.scopes,
          denyScopes: policy.denyScopes,
          onDuty: policy.onDuty,
          canReceiveAssets: policy.canReceiveAssets,
          discloseAI: policy.discloseAI,
          addressAs: policy.addressAs,
          toneOverride: policy.toneOverride,
          tabooTopics: policy.tabooTopics,
          note: policy.note,
          updatedAt: policy.updatedAt,
        };
      })
      .sort((a, b) => (a.name || a.uid).localeCompare(b.name || b.uid));
    return {
      defaults: doc.defaults,
      contacts,
      scopes: access.SCOPES.slice(),
      onDutyModes: access.ON_DUTY_MODES.slice(),
      discloseModes: access.DISCLOSE_MODES.slice(),
    };
  }

  upsertPolicy(body = {}) {
    const uid = String(body.uid || '').trim();
    if (!uid || uid.length > 120) throw fail('缺少对象 ID（联系人或群 ID）');
    if (body.onDuty !== undefined && !access.ON_DUTY_MODES.includes(String(body.onDuty))) {
      throw fail('onDuty 只能是 off / draft / auto');
    }
    if (body.discloseAI !== undefined && !access.DISCLOSE_MODES.includes(String(body.discloseAI))) {
      throw fail('discloseAI 只能是 never / onAsk / always');
    }
    const doc = access.loadDoc(this.config);
    const existing = doc.contacts[uid] && typeof doc.contacts[uid] === 'object' ? doc.contacts[uid] : {};
    const merged = {
      uid,
      name: body.name === undefined ? existing.name : body.name,
      scopes: body.scopes === undefined ? existing.scopes : body.scopes,
      denyScopes: body.denyScopes === undefined ? existing.denyScopes : body.denyScopes,
      onDuty: body.onDuty === undefined ? existing.onDuty : body.onDuty,
      canReceiveAssets: body.canReceiveAssets === undefined ? existing.canReceiveAssets : body.canReceiveAssets,
      discloseAI: body.discloseAI === undefined ? existing.discloseAI : body.discloseAI,
      addressAs: body.addressAs === undefined ? existing.addressAs : body.addressAs,
      toneOverride: body.toneOverride === undefined ? existing.toneOverride : body.toneOverride,
      tabooTopics: body.tabooTopics === undefined ? existing.tabooTopics : body.tabooTopics,
      note: body.note === undefined ? existing.note : body.note,
      updatedAt: Date.now(),
    };
    const contact = access.normalizeContact(uid, merged, Date.now());
    doc.contacts[uid] = contact;
    access.saveDoc(this.config, doc);
    this.logger?.info?.(`[self] policy saved uid=${uid.slice(0, 6)}*** scopes=${contact.scopes.join(',')} onDuty=${contact.onDuty}`);
    return contact;
  }

  removePolicy(uid) {
    const key = String(uid || '').trim();
    if (!key) throw fail('缺少对象 ID');
    const doc = access.loadDoc(this.config);
    if (!Object.prototype.hasOwnProperty.call(doc.contacts, key)) return { removed: false };
    delete doc.contacts[key];
    access.saveDoc(this.config, doc);
    // 契约 §11.5：删策略只回到默认 scope / off，不删 L2 记忆。
    this.logger?.info?.(`[self] policy removed uid=${key.slice(0, 6)}***（L2 记忆保留）`);
    return { removed: true };
  }

  /* ------------------------------ 可分享资产 ------------------------------ */

  assetsSnapshot() {
    const doc = access.loadDoc(this.config);
    const assets = selfModel.loadAssets(this.config).map((asset) => {
      const audience = access.audienceSize(doc, asset.scopes);
      return {
        id: asset.id,
        title: asset.title,
        kind: asset.kind,
        mime: asset.mime,
        size: asset.size,
        scopes: asset.scopes,
        keywords: asset.keywords,
        note: asset.note,
        createdAt: asset.createdAt,
        audience,
        // 启动器用 availableTo 显示「多少个对象可见」；和 audience 同值，保留两个名字。
        availableTo: audience,
      };
    });
    return {
      assets: assets.sort((a, b) => b.createdAt - a.createdAt),
      limits: { maxAssets: selfModel.MAX_ASSETS, maxBytes: selfModel.MAX_ASSET_BYTES },
    };
  }

  importAsset(body = {}) {
    const asset = selfModel.importAsset(this.config, body);
    this.logger?.info?.(`[self] asset imported id=${asset.id} kind=${asset.kind} size=${asset.size} scopes=${asset.scopes.join(',')}`);
    return asset;
  }

  removeAsset(id) {
    const result = selfModel.removeAsset(this.config, id);
    if (result.removed) this.logger?.info?.(`[self] asset removed id=${result.asset.id}`);
    return result;
  }

  /* ------------------------------ 在岗 ------------------------------ */

  onDutySnapshot() {
    const cfg = (this.config && this.config.onDuty) || {};
    const hours = selfModel.workingHours(this.config);
    const all = selfModel.loadPending(this.config).items
      .slice()
      .sort((a, b) => b.at - a.at);
    // queued 条目回读提醒队列的回执；终态写回 pending.json，重启后不用再猜。
    const reminders = this.runtime && this.runtime.reminders;
    for (const item of all) {
      if (item.status !== 'queued' || !item.reminderId) continue;
      const receipt = this.deliveryOf(reminders, item.reminderId);
      // 提醒记录被清理时保持 queued：没有证据就不能臆造失败。
      if (!receipt) continue;
      const state = receiptState(receipt.status);
      if (!state) continue;
      item.status = state;
      item.error = String(receipt.error == null ? '' : receipt.error).slice(0, 200);
      item.finishedAt = Number(receipt.finishedAt) || Date.now();
      selfModel.updatePending(this.config, item.id, {
        status: item.status,
        error: item.error,
        finishedAt: item.finishedAt,
      });
    }
    const withDelivery = (item) => ({
      ...item,
      delivery: {
        state: item.status,
        error: item.error || '',
        at: Number(item.finishedAt) || 0,
      },
    });
    // pending 只留还能操作的；items 是给主人看的历史（最新在前，上限 50）。
    const pending = all.filter((item) => item.status === 'pending').map(withDelivery);
    const items = all
      .filter((item) => ON_DUTY_ITEM_STATES.has(item.status))
      .slice(0, 50)
      .map(withDelivery);
    return {
      enabled: cfg.enabled === true,
      workingHours: {
        start: time.formatClock(hours.start),
        end: time.formatClock(hours.end),
        days: hours.days.slice(),
      },
      sensitiveTopics: selfModel.sensitiveTopics(this.config),
      pending,
      items,
      maxPending: selfModel.MAX_PENDING,
    };
  }

  /** 兼容旧 runtime：优先用 ReminderService.delivery，没有就只读 items。 */
  deliveryOf(reminders, id) {
    if (!reminders || !id) return null;
    if (typeof reminders.delivery === 'function') {
      return reminders.delivery(id);
    }
    const rows = Array.isArray(reminders.items) ? reminders.items : [];
    const row = rows.find((item) => item.id === id);
    return row ? { status: row.status, error: row.error, finishedAt: row.finishedAt } : null;
  }

  updateOnDuty(body = {}) {
    const current = (this.config && this.config.onDuty) || {};
    const next = {
      enabled: body.enabled === undefined ? current.enabled === true : body.enabled === true,
      workingHours: body.workingHours === undefined ? current.workingHours : body.workingHours,
      sensitiveTopics: body.sensitiveTopics === undefined ? current.sensitiveTopics : body.sensitiveTopics,
    };
    const hours = next.workingHours && typeof next.workingHours === 'object' ? next.workingHours : {};
    const start = time.parseClock(hours.start);
    const end = time.parseClock(hours.end);
    if (start === null || end === null) throw fail('工作时段要填 HH:mm（例如 09:00）');
    const days = Array.isArray(hours.days)
      ? [...new Set(hours.days.map((d) => Number(d)).filter((d) => Number.isInteger(d) && d >= 0 && d <= 6))]
      : [];
    if (Array.isArray(hours.days) && hours.days.length && !days.length) throw fail('工作日的取值是 0~6');
    const topics = Array.isArray(next.sensitiveTopics) ? next.sensitiveTopics : [];
    const cleanTopics = [];
    for (const item of topics) {
      const topic = String(item == null ? '' : item).trim();
      if (topic && !cleanTopics.includes(topic)) cleanTopics.push(topic.slice(0, 20));
      if (cleanTopics.length >= 50) break;
    }
    const patch = {
      enabled: next.enabled,
      workingHours: {
        start: time.formatClock(start),
        end: time.formatClock(end),
        days: days.length ? days : [1, 2, 3, 4, 5],
      },
      sensitiveTopics: cleanTopics,
    };
    // 走统一配置写入：写 config.json、清 settings 影子、热重载 runtime.config。
    const runtime = this.runtime || { config: this.config, logger: this.logger };
    configPatch.applyConfigPatch(runtime, { onDuty: patch });
    return {
      enabled: patch.enabled,
      workingHours: patch.workingHours,
      sensitiveTopics: patch.sensitiveTopics,
    };
  }

  /* ------------------------------ 待确认队列 ------------------------------ */

  /**
   * 主人确认一条草稿：直接用现有主动发送队列（提醒）交给桥接。
   * 不自动重试；入队失败会把错误原样返回，条目保持 pending。
   */
  sendPending(runtime, id, body = {}) {
    const list = selfModel.loadPending(this.config).items;
    const item = list.find((row) => row.id === String(id || ''));
    if (!item) throw fail('找不到这条待确认消息', 404);
    if (item.status !== 'pending') throw fail('这条已经处理过了', 409);
    const text = String(body.text == null ? item.text : body.text).trim();
    if (!text) throw fail('发送内容不能为空');
    if (text.length > 4000) throw fail('发送内容太长');
    const group = /:g:/.test(item.key);
    const reminders = runtime && runtime.reminders;
    if (!reminders || typeof reminders.enqueueNow !== 'function') throw fail('发送队列还没准备好', 503);
    const queued = reminders.enqueueNow({
      wxid: item.uid || item.key.split(':').pop(),
      name: item.name,
      chatType: group ? 'group' : 'private',
      text,
      kind: 'onDuty',
    });
    // 附件：只发权限仍允许、且对象 canReceiveAssets 的。
    const doc = access.loadDoc(this.config);
    const policy = access.policyFor(doc, item.uid);
    for (const ref of item.assets || []) {
      const resolved = selfModel.resolveAsset(this.config, ref.id, policy);
      if (!resolved.ok) {
        this.logger?.warn?.(`[self] pending asset skipped id=${ref.id} reason=${resolved.reason}`);
        continue;
      }
      this.queuePendingAsset(runtime, resolved, item);
    }
    const updated = selfModel.updatePending(this.config, item.id, {
      text,
      status: 'queued',
      reminderId: queued.id,
      error: '',
      finishedAt: 0,
    });
    // 发出去的内容也写进会话记忆，否则下一轮模型不知道主人已经回过这条。
    try {
      if (runtime && runtime.store && item.key) {
        const model = selfModel.load(this.config);
        runtime.store.appendMessage(item.key, {
          role: 'assistant',
          name: (model.identity && model.identity.name) || this.config.persona.name,
          text,
          ts: Date.now(),
        });
      }
    } catch (err) {
      this.logger?.warn?.('[self] 待确认发送后写入记忆失败：' + describeError(err));
    }
    return updated;
  }

  discardPending(id) {
    const list = selfModel.loadPending(this.config).items;
    const item = list.find((row) => row.id === String(id || ''));
    if (!item) throw fail('找不到这条待确认消息', 404);
    if (item.status !== 'pending') throw fail('这条已经处理过了', 409);
    return selfModel.updatePending(this.config, item.id, { status: 'discarded' });
  }

  /** 清除已处理的回执（queued / sent / failed / uncertain）；pending 必须先发送或丢弃。 */
  dismissPending(id) {
    const list = selfModel.loadPending(this.config).items;
    const item = list.find((row) => row.id === String(id || ''));
    if (!item) throw fail('找不到这条待确认消息', 404);
    if (item.status === 'pending') throw fail('待处理的条目不能清除，请先发送或丢弃', 409);
    return selfModel.updatePending(this.config, item.id, { status: 'discarded' });
  }

  /** 待确认里的附件走桥接的通用附件通道；源文件仍在 data/share/files。 */
  queuePendingAsset(runtime, resolved, item) {
    const service = runtime && runtime.stickerService;
    if (!service) return null;
    try {
      return service.enqueueAsset({
        path: resolved.file,
        title: resolved.asset.title,
        chat: item.name || item.uid,
        chatUsername: item.uid,
        key: item.key,
      });
    } catch (err) {
      this.logger?.warn?.('[self] 待确认附件入队失败：' + describeError(err));
      return null;
    }
  }
}

module.exports = { SelfService };
