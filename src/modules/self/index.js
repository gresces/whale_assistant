'use strict';

/**
 * 分身后端模块的公开入口。
 * 组合根只需要 SelfService；路由细节留在 routes.js。
 */
const { SelfService } = require('./service');
const manifest = require('./manifest');

module.exports = { SelfService, create: manifest.create };
