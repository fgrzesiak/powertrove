// Persistent browser companion session. Activated only after user explicitly chooses
// "Connect to PowerTrove". Never reads page contents until the desktop requests context.
(() => {
  "use strict";
  const HOST = "com.powertrove.browser";
  const protocol = globalThis.PowerTroveBrowserProtocol;
  let port = null, retry = null, retries = 0, enabled = false, ready = false;
  const documentIds = new Map();
  const canceled = new Set();
  const idForTab = id => {
    if (!documentIds.has(id)) documentIds.set(id, crypto.randomUUID());
    return documentIds.get(id);
  };
  chrome.tabs.onUpdated?.addListener((id,change) => {
    if(change.status === "loading" || change.url) documentIds.delete(id);
  });
  chrome.tabs.onRemoved?.addListener(id => documentIds.delete(id));

  const browser = /Edg\//.test(navigator.userAgent) ? "edge" : "chrome";
  const originAllowed = url => {
    try { const parsed = new URL(url); return parsed.protocol === "https:" || parsed.protocol === "http:"; }
    catch { return false; }
  };
  async function captureContext() {
    if (!chrome.windows?.getLastFocused) throw Error("restricted");
    const win = await chrome.windows.getLastFocused({populate:true,windowTypes:["normal"]});
    if (!win || win.incognito || !Array.isArray(win.tabs)) throw Error("restricted");
    const tab = win.tabs.find(item => item.active);
    if (!tab || !originAllowed(tab.url) || !tab.title || tab.incognito) throw Error("restricted");
    let selection = "";
    let signals = {};
    try {
      const permitted = await chrome.permissions.contains({origins:["http://*/*","https://*/*"]});
      if (permitted) {
        const response = await chrome.scripting.executeScript({
          target:{tabId:tab.id},
          func:() => {
            const select = String(window.getSelection()?.toString() || "").slice(0,24576);
            const tables = [...document.querySelectorAll("table")].slice(0,100);
            const sizes = tables.map(t => {
              const rows = t.rows.length;
              return {rows,columns:Math.max(0,...[...t.rows].slice(0,20).map(row=>row.cells.length))};
            }).sort((a,b)=>b.rows*b.columns-a.rows*a.columns);
            const main = document.querySelector("article") || document.querySelector("main");
            const words = String((main || document.body)?.innerText || "").trim()
              .split(/\s+/).filter(Boolean).length;
            return {selection:select, signals:{
              isArticle:!!main,tableCount:tables.length,
              largestTableRows:Math.min(5000,sizes[0]?.rows || 0),
              largestTableColumns:Math.min(200,sizes[0]?.columns || 0),
              imageCount:Math.min(10000,document.images.length),
              codeBlockCount:Math.min(10000,document.querySelectorAll("pre,code").length),
              wordCount:Math.min(1000000,words),
              language:String(document.documentElement.lang || "").slice(0,24)
            }};
          }
        });
        selection = String(response?.[0]?.result?.selection || "").slice(0,24576);
        signals = response?.[0]?.result?.signals || {};
      }
    } catch { /* Metadata-only mode when access is denied or unsupported. */ }
    return {
      title:String(tab.title).slice(0,300),
      windowTitle:String(tab.title).slice(0,300),
      url:tab.url.slice(0,2048),
      selection, signals, focused:!!win.focused, incognito:false,
      tabId:tab.id,windowId:win.id,documentId:idForTab(tab.id)
    };
  }
  function transmit(frame) {
    if (port && protocol.validate(frame)) port.postMessage(frame);
  }
  async function performRequest(frame) {
    if (frame.action === "getContext") return await captureContext();
    const binary = ["captureVisible","captureFullPage","saveMhtml"].includes(frame.action);
    if (!binary && !["extractArticle","extractPage","extractTables","listLinks","listImages"].includes(frame.action)
      || !frame.tab) throw Error("failed");
    const tab = await chrome.tabs.get(frame.tab.tabId);
    if (!tab || tab.windowId !== frame.tab.windowId || tab.incognito ||
        !originAllowed(tab.url) || idForTab(tab.id) !== frame.tab.documentId)
      throw Error("navigated");
    if (!await chrome.permissions.contains({origins:["http://*/*","https://*/*"]}))
      throw Error("permission");
    if (binary) {
      // Without a click in the extension, activeTab never applies; Chrome then allows
      // captureVisibleTab only with <all_urls>, which http(s)://*/* does not satisfy.
      if (frame.action !== "saveMhtml" && !await chrome.permissions.contains({origins:["<all_urls>"]}))
        throw Error("permission");
      const focused = await chrome.tabs.query({active:true,windowId:tab.windowId});
      if (focused.length !== 1 || focused[0].id !== tab.id) throw Error("navigated");
      let bytes,kind;
      if (frame.action === "saveMhtml") {
        kind="mhtml";
        const archive = await chrome.pageCapture.saveAsMHTML({tabId:tab.id});
        bytes = new Uint8Array(await archive.arrayBuffer());
      } else {
        kind="image";
        const data = frame.action==="captureVisible"
          ? await (globalThis.PowerTroveCaptureVisible?.(tab) ??
            chrome.tabs.captureVisibleTab(tab.windowId,{format:"png"}))
          : await globalThis.PowerTroveCaptureFullPage?.(tab);
        if (typeof data !== "string" || !data.startsWith("data:image/png;base64,"))
          throw Error("failed");
        bytes = new Uint8Array(await (await fetch(data)).arrayBuffer());
      }
      const latest=await chrome.tabs.get(tab.id);
      if (!latest || latest.url !== tab.url || idForTab(tab.id) !== frame.tab.documentId)
        throw Error("navigated");
      const maximum=kind==="mhtml"?protocol.Limits.mhtmlBytes:protocol.Limits.imageBytes;
      if(bytes.length===0 || bytes.length>maximum)throw Error("tooLarge");
      return {binary:{kind,bytes}};
    }
    const operation = ({
      extractArticle:"article", extractPage:"markdown", extractTables:"tables",
      listLinks:"links", listImages:"images"
    })[frame.action];
    await chrome.scripting.executeScript({target:{tabId:tab.id},files:["content.js"]});
    const response = await chrome.tabs.sendMessage(tab.id,{target:"powertrove-content-v1",action:operation});
    if (!response?.ok) throw Error("restricted");
    const after = await chrome.tabs.get(tab.id);
    if (!after || after.url!==tab.url || idForTab(tab.id)!==frame.tab.documentId)
      throw Error("navigated");
    const result={content:response.result};
    if(new TextEncoder().encode(JSON.stringify(result)).length>700000)throw Error("tooLarge");
    return result;
  }
  // Encode a native frame without passing 64 KiB as function arguments at once.
  const chunkBase64 = input => {
    let binary="";
    for(let i=0;i<input.length;i+=8192)
      binary+=String.fromCharCode(...input.subarray(i,i+8192));
    return btoa(binary);
  };
  async function onRequest(frame) {
    try {
      const result = await Promise.race([
        performRequest(frame),
        new Promise((_,reject)=>setTimeout(()=>reject(Error("timeout")),
          Math.max(1,Math.min(30000,frame.deadlineMs))))
      ]);
      if (canceled.has(frame.id))throw Error("canceled");
      if (result.binary) {
        const {kind,bytes}=result.binary;
        // Each frame stays well below native messaging and worker IPC limits.
        const size=64*1024;
        for(let offset=0,seq=0;offset<bytes.length;offset+=size,seq++){
          if(canceled.has(frame.id))throw Error("canceled");
          if(seq>1024)throw Error("tooLarge");
          const portion=bytes.subarray(offset,Math.min(bytes.length,offset+size));
          transmit({type:"chunk",id:frame.id,seq,last:offset+size>=bytes.length,
            kind,base64:chunkBase64(portion)});
        }
        transmit({type:"response",id:frame.id,ok:true,result:{kind,bytes:bytes.length}});
      } else transmit({type:"response",id:frame.id,ok:true,result});
    } catch(error) {
      const code=["restricted","permission","navigated","timeout","tooLarge","canceled"]
        .includes(error?.message)?error.message:"failed";
      transmit({type:"response",id:frame.id,ok:false,error:code});
    } finally {canceled.delete(frame.id);}
  }
  function disconnect() {
    if (port) { try { port.disconnect(); } catch{} }
    port = null;
    ready = false;
  }
  function schedule() {
    if (!enabled || retry !== null) return;
    retries = Math.min(retries+1,7);
    const delay = Math.min(60000, 1000*2**retries);
    retry = setTimeout(()=>{retry=null;start();},delay);
  }
  function start() {
    if (!enabled || port) return;
    try {
      const session = chrome.runtime.connectNative(HOST);
      port = session;
      session.onMessage.addListener(message => {
        if (!protocol.validate(message)) { disconnect();schedule();return; }
        if (message.type === "ready") {
          if (protocol.negotiate(message.minProtocol,message.maxProtocol) === null) disconnect();
          else { retries = 0; ready = true; }
        } else if (message.type === "request") {
          void onRequest(message);
        } else if (message.type === "cancel") {
          canceled.add(message.id);
        }
      });
      session.onDisconnect.addListener(() => {
        if (port === session) { port=null; ready=false; schedule(); }
      });
      transmit({
        type:"hello",browser,
        extensionVersion:chrome.runtime.getManifest().version,
        minProtocol:1,maxProtocol:1,capabilities:["context.fast"]
      });
    } catch { port=null;schedule(); }
  }
  async function enable() {
    enabled = true;
    await chrome.storage.local.set({browserCompanionEnabled:true});
    start();
  }
  async function load() {
    const saved = await chrome.storage.local.get("browserCompanionEnabled");
    enabled = saved.browserCompanionEnabled === true;
    if (enabled) start();
  }
  globalThis.PowerTroveCompanion = Object.freeze({
    status:() => ({enabled,connected:ready}),
    enable,load,stop:async()=>{
    enabled=false;
    await chrome.storage.local.set({browserCompanionEnabled:false});
    if(retry!==null)clearTimeout(retry);retry=null;disconnect();
  }});
})();
