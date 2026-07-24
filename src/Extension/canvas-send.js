(function () {
  if (window.__canvasSendInstalled) return;
  window.__canvasSendInstalled = true;

  var MAX=64000;
  window.canvasSend=function (action, payload) {
    var msg=Object.assign({}, payload, { action: action });
    var size=JSON.stringify(msg).length;
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
  };
})();
