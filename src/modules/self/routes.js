'use strict';

/**
 * 分身管理路由（契约 §7）。
 *
 * 全部只允许本机回环访问：自我模型、联系人策略、待确认草稿都是主人数据，
 * 放给局域网等于把「谁能收到什么」和主人的草稿一起交出去。
 */
const { json, readBody } = require('../../http/protocol');
const { isLoopbackRequest } = require('../../http/security');
const { SelfService } = require('./service');

function serviceFor(runtime) {
  if (!runtime.selfService) {
    runtime.selfService = new SelfService(runtime.config, {
      logger: runtime.logger && typeof runtime.logger.child === 'function'
        ? runtime.logger.child('self')
        : runtime.logger,
      runtime,
    });
  }
  return runtime.selfService;
}

function idFrom(pathname, prefix) {
  const rest = pathname.slice(prefix.length);
  try {
    return decodeURIComponent(rest);
  } catch {
    return rest;
  }
}

module.exports = function register(router) {
  router.pattern(/^\/api\/self(?:\/.*)?$/, async (runtime, req, res, info) => {
    if (!isLoopbackRequest(req)) {
      return json(res, 403, { ok: false, error: '分身管理仅允许在本机操作' });
    }
    const service = serviceFor(runtime);
    const pathname = info.pathname;
    const method = req.method || 'GET';

    try {
      if (pathname === '/api/self' && method === 'GET') {
        return json(res, 200, { ok: true, ...service.modelSnapshot() });
      }
      if (pathname === '/api/self' && method === 'POST') {
        const body = await readBody(req);
        const result = service.saveModel(body);
        return json(res, 200, { ok: true, ...result });
      }

      if (pathname === '/api/self/policy' && method === 'GET') {
        return json(res, 200, { ok: true, ...service.policySnapshot() });
      }
      if (pathname === '/api/self/policy' && method === 'POST') {
        const body = await readBody(req);
        return json(res, 200, { ok: true, contact: service.upsertPolicy(body) });
      }
      if (pathname.startsWith('/api/self/policy/') && method === 'DELETE') {
        const uid = idFrom(pathname, '/api/self/policy/');
        return json(res, 200, { ok: true, ...service.removePolicy(uid) });
      }

      if (pathname === '/api/self/assets' && method === 'GET') {
        return json(res, 200, { ok: true, ...service.assetsSnapshot() });
      }
      if (pathname === '/api/self/assets' && method === 'POST') {
        const body = await readBody(req);
        return json(res, 200, { ok: true, asset: service.importAsset(body) });
      }
      if (pathname.startsWith('/api/self/assets/') && method === 'DELETE') {
        const id = idFrom(pathname, '/api/self/assets/');
        return json(res, 200, { ok: true, ...service.removeAsset(id) });
      }

      if (pathname === '/api/self/on-duty' && method === 'GET') {
        return json(res, 200, { ok: true, ...service.onDutySnapshot() });
      }
      if (pathname === '/api/self/on-duty' && method === 'POST') {
        const body = await readBody(req);
        return json(res, 200, { ok: true, ...service.updateOnDuty(body) });
      }
      if (pathname.startsWith('/api/self/on-duty/pending/') && method === 'POST') {
        const id = idFrom(pathname, '/api/self/on-duty/pending/');
        const body = await readBody(req);
        const action = String(body.action || '').trim();
        if (action === 'send') {
          const item = service.sendPending(runtime, id, body);
          return json(res, 200, { ok: true, item });
        }
        if (action === 'discard') {
          const item = service.discardPending(id);
          return json(res, 200, { ok: true, item });
        }
        if (action === 'dismiss') {
          const item = service.dismissPending(id);
          return json(res, 200, { ok: true, item });
        }
        return json(res, 400, { ok: false, error: 'action 只能是 send / discard / dismiss' });
      }

      return json(res, 404, { ok: false, error: 'not found' });
    } catch (err) {
      const status = Number(err && err.statusCode) || 500;
      return json(res, status, {
        ok: false,
        error: (err && err.message) || '分身接口失败',
        ...(err && err.code ? { code: err.code } : {}),
      });
    }
  });
};
