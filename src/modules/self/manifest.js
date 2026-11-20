'use strict';

module.exports = {
  id: 'self',
  order: 190,
  create(runtime, { logger } = {}) {
    const { SelfService } = require('./service');
    return new SelfService(runtime.config, { logger, runtime });
  },
  routes: require('./routes'),
};
