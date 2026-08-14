(function () {
  if (window.__canvasSendInstalled) return;
  window.__canvasSendInstalled = true;

  var MAX=64000;
  window.canvasSend=function (action, payload) {
    if (typeof action !== 'string' || !action.trim()) {
      console.error('[canvas] canvasSend DROPPED: action must be a nonblank string');
      return false;
    }
    if (
      window.parent === window &&
      window.__canvasTopLevelTransportAvailable !== true
    ) {
      console.error(
        '[canvas] canvasSend DROPPED: parent transport unavailable'
      );
      return false;
    }
    try {
      var msg=Object.assign({}, payload || {}, { action: action });
      var serialized=JSON.stringify(msg);
      if(typeof serialized!=='string') {
        throw new TypeError('message did not serialize to JSON');
      }
      var size=serialized.length;
      if(size>MAX) {
        console.error(
          '[canvas] canvasSend DROPPED: ' +
          action +
          ' message too large (' +
          size +
          ' > ' +
          MAX +
          ' UTF-16 code units); not sent'
        );
        return false;
      }
      window.parent.postMessage(msg, '*');
      return true;
    } catch (error) {
      console.error('[canvas] canvasSend DROPPED: message is not serializable', error);
      return false;
    }
  };
})();
