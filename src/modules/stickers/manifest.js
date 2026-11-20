'use strict';

module.exports = {
  id: 'stickers',
  order: 120,
  create(runtime, { store, logger }) {
    const { StickerService } = require('./service');
    const service = new StickerService(runtime.config, { store, logger: logger.child('sticker') });
    // 模块初始化时把资产目录建出来（只建空目录，不删已有文件）；
    // 构造函数本身仍保持零磁盘 I/O，这里已经是组合根装配阶段。
    service.ensureShareDir();
    return service;
  },
  routes: require('./routes'),
};
