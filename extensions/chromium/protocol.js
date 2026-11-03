// Browser Companion wire-contract validation. No browser APIs, network requests,
// page content logging, eval, or executable remote code. Loaded by MV3 in WP2.
(() => {
  "use strict";
  const Limits = Object.freeze({
    protocol: 1, minProtocol: 1, messageBytes: 960 * 1024,
    chunkBytes: 512 * 1024, textBytes: 2 * 1024 * 1024,
    imageBytes: 24 * 1024 * 1024, mhtmlBytes: 64 * 1024 * 1024,
    rows: 5000, columns: 200, links: 200, images: 100
  });
  const ops = new Set(["getContext", "captureVisible", "captureFullPage", "captureArea",
    "saveMhtml", "extractArticle", "extractPage", "extractTables", "listLinks",
    "listImages", "createPdf", "cancelCapture"]);
  const errors = new Set(["restricted", "permission", "navigated", "timeout",
    "tooLarge", "canceled", "failed"]);
  const kinds = new Set(["image", "mhtml", "text", "pdf"]);
  const ownKeys = (object, keys) => object !== null && typeof object === "object" &&
    !Array.isArray(object) && Object.keys(object).length === keys.length &&
    keys.every(key => Object.prototype.hasOwnProperty.call(object, key));
  const allowedKeys = (object, required, optional = []) => object !== null &&
    typeof object === "object" && !Array.isArray(object) &&
    required.every(key => Object.prototype.hasOwnProperty.call(object, key)) &&
    Object.keys(object).every(key => required.includes(key) || optional.includes(key));
  const unicode = text => {
    for (let i = 0; i < text.length; i++) {
      const unit = text.charCodeAt(i);
      if (unit >= 0xd800 && unit <= 0xdbff) {
        if (++i >= text.length) return false;
        const next = text.charCodeAt(i);
        if (next < 0xdc00 || next > 0xdfff) return false;
      } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
    }
    return true;
  };
  const bounded = (value, max) => typeof value === "string" &&
    value.length > 0 && value.length <= max && !value.includes("\0") && unicode(value);
  const id = value => typeof value === "string" &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
    value.toLowerCase() !== "00000000-0000-0000-0000-000000000000";
  const int = (value, min, max) => Number.isSafeInteger(value) && value >= min && value <= max;
  const capabilities = items => Array.isArray(items) && items.length <= 16 &&
    items.every(item => typeof item === "string" && /^[a-z][a-zA-Z0-9.-]{0,49}$/.test(item)) &&
    new Set(items).size === items.length;
  const version = text => typeof text === "string" && /^\d+\.\d+(?:\.\d+){0,2}$/.test(text) &&
    text.length <= 32;
  const tab = value => ownKeys(value, ["sessionId", "windowId", "tabId", "documentId"]) &&
    id(value.sessionId) && int(value.windowId,1,2147483647) &&
    int(value.tabId,0,2147483647) && bounded(value.documentId,128);
  const base64 = value => {
    if (!bounded(value,710000) || value.length % 4 ||
      value.length > 4 * Math.ceil(Limits.chunkBytes/3)) return false;
    try {
      const bytes = atob(value);
      return bytes.length > 0 && bytes.length <= Limits.chunkBytes &&
        btoa(bytes) === value;
    } catch { return false; }
  };
  const validate = frame => {
    if (!frame || typeof frame !== "object" || Array.isArray(frame)) return false;
    switch(frame.type) {
      case "hello":
        return ownKeys(frame, ["type", "browser", "extensionVersion", "minProtocol", "maxProtocol", "capabilities"]) &&
          ["chrome","edge"].includes(frame.browser) && version(frame.extensionVersion) &&
          int(frame.minProtocol,1,999) && int(frame.maxProtocol,frame.minProtocol,999) &&
          capabilities(frame.capabilities);
      case "ready":
        return ownKeys(frame, ["type", "minProtocol", "maxProtocol", "capabilities"]) &&
          int(frame.minProtocol,1,999) && int(frame.maxProtocol,frame.minProtocol,999) &&
          capabilities(frame.capabilities);
      case "request":
        return allowedKeys(frame,["type","id","action","deadlineMs"],["tab","windowTitle"]) &&
          id(frame.id) && ops.has(frame.action) && int(frame.deadlineMs,1,30000) &&
          (!("tab" in frame) || tab(frame.tab)) &&
          (!("windowTitle" in frame) || bounded(frame.windowTitle,300));
      case "response":
        return (ownKeys(frame,["type","id","ok","result"]) ||
          ownKeys(frame,["type","id","ok","error"])) &&
          id(frame.id) && typeof frame.ok === "boolean" &&
          (frame.ok ? Object.prototype.hasOwnProperty.call(frame,"result") &&
            frame.result !== null && typeof frame.result === "object" &&
            !Array.isArray(frame.result) : errors.has(frame.error));
      case "cancel":
        return ownKeys(frame,["type","id"]) && id(frame.id);
      case "chunk":
        return ownKeys(frame,["type","id","seq","last","kind","base64"]) &&
          id(frame.id) && int(frame.seq,0,1024) && typeof frame.last === "boolean" &&
          kinds.has(frame.kind) && base64(frame.base64);
      default: return false;
    }
  };
  const decode = input => {
    try {
      const frame = typeof input === "string" ? JSON.parse(input) : input;
      if (!validate(frame) || new TextEncoder().encode(JSON.stringify(frame)).length > Limits.messageBytes)
        return null;
      return frame;
    } catch { return null; }
  };
  const encode = frame => {
    if (!validate(frame)) throw new Error("Invalid Browser Companion frame");
    const json = JSON.stringify(frame);
    if (new TextEncoder().encode(json).length > Limits.messageBytes)
      throw new Error("Browser Companion frame too large");
    return json;
  };
  const negotiate = (min,max) => !int(min,1,999) || !int(max,min,999) ? null :
    Math.min(Limits.protocol,max) < Math.max(Limits.minProtocol,min) ? null :
    Math.min(Limits.protocol,max);
  const totalLimit = kind => kind === "mhtml" ? Limits.mhtmlBytes :
    kind === "text" ? Limits.textBytes :
    kind === "image" || kind === "pdf" ? Limits.imageBytes : null;

  globalThis.PowerTroveBrowserProtocol = Object.freeze({
    Limits, validate, decode, encode, negotiate, totalLimit
  });
})();
