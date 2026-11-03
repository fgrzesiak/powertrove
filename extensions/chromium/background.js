if (typeof importScripts === "function") importScripts("protocol.js","companion.js");
// PowerTrove Browser Pack — MV3 service worker. All actions are initiated by a click,
// context menu or extension shortcut. No background content scraping or host messaging.
const HOST = "com.powertrove.browser";
const MAX_DOWNLOAD_BYTES = 24 * 1024 * 1024;
const MAX_FULL_PIXELS = 20000000;
const activeTab = async () => {
  const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
  if (!tab?.id || !/^https?:\/\//i.test(tab.url || "")) {
    throw new Error("Open a normal website (http/https) first.");
  }
  return tab;
};
const safeName = (name, extension) => {
  const value = String(name || "webpage").replace(/[<>:"/\\|?*\x00-\x1f]/g, "").trim()
    .replace(/\.+$/g, "").slice(0, 68) || "webpage";
  return "PowerTrove/" + value + "." + extension;
};
const inject = async (tab, func, args = []) => {
  const response = await chrome.scripting.executeScript({target:{tabId:tab.id},func,args});
  if (!response?.length) throw new Error("Page is not accessible.");
  return response[0].result;
};
const content = async (tab, action) => {
  await chrome.scripting.executeScript({target:{tabId:tab.id},files:["content.js"]});
  const result = await chrome.tabs.sendMessage(tab.id,{target:"powertrove-content-v1",action});
  if (!result?.ok) throw new Error(result?.error || "Page content could not be extracted.");
  return result.result;
};
// Ordinary browser exports work without PowerTrove Desktop. Ask the user
// for a filename/location by default; the local setting can opt into the
// browser's Downloads folder. Native handoffs and explicit multi-image
// batches are separate actions and never open dozens of Save As dialogs.
const downloadUrl = async (url, filename, {direct = false} = {}) => {
  const saved = direct ? null : await chrome.storage.local.get({askWhereToSave:true})
    .catch(()=>({askWhereToSave:true})); // Storage failure must not silently switch to a direct save.
  const saveAs = !direct && saved.askWhereToSave !== false;
  const id = await chrome.downloads.download({
    url,filename,conflictAction:"uniquify",saveAs
  });
  if (!Number.isInteger(id) || id < 0) throw new Error("Download could not start.");
  return id;
};
const downloadText = async (text, filename, mime = "text/markdown") => {
  if (typeof text !== "string" || text.length > 250000) throw new Error("Export is too large.");
  const url = "data:" + mime + ";charset=utf-8," + encodeURIComponent(text);
  await downloadUrl(url,filename);
};
const bytesToDataUrl = async (blob, type) => {
  if (!blob || blob.size > MAX_DOWNLOAD_BYTES) throw new Error("Export exceeds 24 MB.");
  const bytes = new Uint8Array(await blob.arrayBuffer());
  const segments = [];
  for (let i=0; i<bytes.length; i+=12288) {
    segments.push(btoa(String.fromCharCode(...bytes.subarray(i, i+12288))));
  }
  return "data:" + type + ";base64," + segments.join("");
};
// captureVisibleTab allows at most two calls per second in Chromium and rejects
// the rest. Retrying shortly after a rejection uses that quota exactly; a fixed
// spacing between calls wastes time because Chromium counts a sliding window.
let captureQueue = Promise.resolve();
const sleep = ms => new Promise(resolve => setTimeout(resolve,ms));
const QUOTA_RETRY_MS = 50;
const QUOTA_RETRIES = 60;
const isCaptureQuotaError = error =>
  String(error?.message ?? error).includes("MAX_CAPTURE_VISIBLE_TAB_CALLS_PER_SECOND");
const screenshot = (tab, withinFullPage = false) => {
  if (fullPageCaptureRunning && !withinFullPage)
    throw new Error("A full-page screenshot is already running.");
  const pending = captureQueue.then(async () => {
    for (let attempt = 0; ; attempt++) {
      try {
        return await chrome.tabs.captureVisibleTab(tab.windowId,{format:"png"});
      } catch (error) {
        if (!isCaptureQuotaError(error) || attempt >= QUOTA_RETRIES) throw error;
        await sleep(QUOTA_RETRY_MS);
      }
    }
  });
  captureQueue = pending.then(()=>{},()=>{});
  return pending;
};
// The authenticated native companion shares the same global capture quota and
// must not interleave with a page whose scroll position we are changing.
globalThis.PowerTroveCaptureVisible = tab => screenshot(tab);
const downloadScreenshot = async (image, tab, suffix, options) => {
  return downloadUrl(image,safeName(tab.title+"-"+suffix,"png"),options);
};
const dimensions = tab => inject(tab, () => ({
  height: Math.max(document.documentElement.scrollHeight, document.body?.scrollHeight||0),
  viewportH: window.innerHeight, viewportW: window.innerWidth,
  scrollX: window.scrollX, scrollY: window.scrollY
}));
const decodedPng = async url => createImageBitmap(await (await fetch(url)).blob());
const pngData = async canvas => bytesToDataUrl(await canvas.convertToBlob({type:"image/png"}), "image/png");

let fullPageCaptureRunning = false;

// CDP returns base64 PNG data. Inspect its IHDR and byte count before accepting
// it; reported CSS dimensions alone do not prove the encoded PNG stayed in bounds.
function safeCdpPng(data, expectedWidth, expectedHeight) {
  if(typeof data!=="string" || !data.length || data.length%4!==0 ||
      data.length>Math.ceil(MAX_DOWNLOAD_BYTES/3)*4) return null;
  const padding=data.endsWith("==")?2:data.endsWith("=")?1:0;
  if(data.length/4*3-padding>MAX_DOWNLOAD_BYTES) return null;
  try {
    const header=atob(data.slice(0,40));
    const signature=[137,80,78,71,13,10,26,10];
    if(signature.some((value,i)=>header.charCodeAt(i)!==value) ||
        header.slice(12,16)!=="IHDR") return null;
    const integer=offset=>(header.charCodeAt(offset)*16777216+
      header.charCodeAt(offset+1)*65536+
      header.charCodeAt(offset+2)*256+header.charCodeAt(offset+3));
    const width=integer(16),height=integer(20);
    if(!width || !height || height>16000 || width*height>MAX_FULL_PIXELS ||
       Math.abs(width-expectedWidth)>2 || Math.abs(height-expectedHeight)>2)
      return null;
    return "data:image/png;base64,"+data;
  }catch{return null;}
}

// Extra waits in ms after two frames; about 1.5 s at most before the scroll method takes over.
const FAST_CAPTURE_WAITS = [0, 100, 300, 1000];

// A capture taken before Chromium painted the enlarged viewport shows the
// visible area again and again. The period is about one viewport but not
// exactly (the debugger bar changes the height), so search for it: if the
// rows with content near the top recur at one fixed distance, the image is stale.
async function repeatsViewport(png, viewportHeight) {
  const bitmap=await createImageBitmap(await (await fetch(png)).blob());
  try {
    const rows=Math.min(bitmap.height,Math.ceil(viewportHeight*2.6));
    if(!(viewportHeight>0) || rows<viewportHeight*1.5) return false;
    const canvas=new OffscreenCanvas(bitmap.width,rows);
    const context=canvas.getContext("2d",{willReadFrequently:true});
    context.drawImage(bitmap,0,0);
    const data=context.getImageData(0,0,bitmap.width,rows).data;
    // One whole pixel per 32-bit value; about 256 sampled pixels per row are
    // enough to tell rows apart and keep this well under the capture time.
    const pixels=new Uint32Array(data.buffer,data.byteOffset,bitmap.width*rows);
    const step=Math.max(1,Math.floor(bitmap.width/256));
    // One hash per row; null for plain background rows, which match anywhere.
    const hashes=new Array(rows);
    for(let y=0;y<rows;y++){
      const start=y*bitmap.width,first=pixels[start];
      let hash=2166136261,plain=true;
      for(let x=0;x<bitmap.width;x+=step){
        const value=pixels[start+x];
        if(value!==first) plain=false;
        hash=Math.imul(hash^value,16777619);
      }
      hashes[y]=plain?null:hash;
    }
    const sample=Math.floor(viewportHeight*.9);
    const content=[];
    for(let y=0;y<sample;y++) if(hashes[y]!==null) content.push(y);
    if(content.length<20) return false;
    for(let period=Math.floor(viewportHeight*.5);period<=Math.ceil(viewportHeight*1.5);period++){
      let same=0;
      for(const y of content) if(y+period<rows && hashes[y]===hashes[y+period]) same++;
      if(same>=content.length*.95) return true;
    }
    return false;
  } finally {
    bitmap.close();
  }
}

async function tryFastFullPageScreenshot(tab) {
  // No popup toggle or no optional browser consent => unchanged slow path.
  const saved=await chrome.storage.local.get({fastFullPageScreenshots:false});
  if(!saved.fastFullPageScreenshots ||
      !await chrome.permissions.contains({permissions:["debugger"]})) return null;

  const target={tabId:tab.id};
  let attached=false,prepared=false,overridden=false,canceledByUser=false,captured=false;
  const onDetach=(source,reason)=>{
    if(source?.tabId===tab.id && reason==="canceled_by_user") canceledByUser=true;
  };
  const throwIfCanceled=()=>{
    if(canceledByUser) {
      const error=new Error(chrome.i18n.getMessage("fastFullPageCanceled") ||
        "Fast screenshot canceled in the browser.");
      error.name="PowerTroveCaptureCanceled";
      throw error;
    }
  };
  try{
    chrome.debugger.onDetach.addListener(onDetach);
    await chrome.debugger.attach(target,"1.3");
    attached=true;
    throwIfCanceled();
    // Reuse #611's isolated-world preparation (pauses animation / unpins
    // sticky elements). Do NOT call .move(): fixed elements are not hidden
    // because CDP paints the page in one pass, without repeating them.
    await chrome.scripting.executeScript({target:{tabId:tab.id},files:["fullpage-capture.js"]});
    prepared=true;
    const page=await inject(tab,()=>({
      ...globalThis.PowerTroveFullPage.begin(),
      scale:globalThis.devicePixelRatio||1
    }));
    throwIfCanceled();
    const layout=await chrome.debugger.sendCommand(target,"Page.getLayoutMetrics");
    throwIfCanceled();
    const width=page?.viewportW,scale=page?.scale;
    const height=Math.ceil((layout?.cssContentSize ?? layout?.contentSize)?.height);
    // Same output size as the scroll method: device pixels of the page width.
    if(!Number.isFinite(width) || !Number.isFinite(height) || !(scale>0) ||
        width<=0 || height<=0 || height>16000 ||
        width*scale*height*scale>MAX_FULL_PIXELS) return null;
    // captureBeyondViewport from an extension's debugger session repeats the
    // visible area instead of painting the rest of the page. Make the viewport
    // as tall as the page instead and capture once it has painted at that size.
    await chrome.debugger.sendCommand(target,"Emulation.setDeviceMetricsOverride",
      {width,height,deviceScaleFactor:scale,mobile:false});
    overridden=true;
    // How long Chromium needs to paint the enlarged page depends on the page
    // (images, tables, fonts) and the device, not on its height. Start right
    // after two frames and wait longer only while the capture is still stale.
    let png=null;
    for(const wait of FAST_CAPTURE_WAITS){
      const settled=await inject(tab,ms=>globalThis.PowerTroveFullPage.settle(ms),[wait]);
      throwIfCanceled();
      // Sections sized to the viewport (100vh) grow with it; the scroll method keeps them right.
      if(!Number.isFinite(settled) || Math.abs(settled-height)>32) return null;
      const shot=await chrome.debugger.sendCommand(target,"Page.captureScreenshot",{format:"png"});
      throwIfCanceled();
      const candidate=safeCdpPng(shot?.data,Math.round(width*scale),Math.round(height*scale));
      if(!candidate) return null;
      if(!await repeatsViewport(candidate,Math.round(page.viewportH*scale))){png=candidate;break;}
    }
    if(!png) return null;
    const latest=await chrome.tabs.get(tab.id);
    throwIfCanceled();
    if(!latest || latest.url!==tab.url) return null;
    captured=true;
    return png;
  }catch(error){
    if(String(error?.message??"").includes("canceled_by_user"))
      canceledByUser=true;
    throwIfCanceled();
    if(error?.name==="PowerTroveCaptureCanceled") throw error;
    // CDP may be denied, attached by DevTools, blocked by policy or fail on
    // special pages. None of these disables the original scroll method.
    return null;
  }finally{
    chrome.debugger?.onDetach?.removeListener(onDetach);
    if(overridden) await chrome.debugger.sendCommand(target,"Emulation.clearDeviceMetricsOverride").catch(()=>{});
    if(attached) await chrome.debugger.detach(target).catch(()=>{});
    if(prepared) await inject(tab,()=>globalThis.PowerTroveFullPage?.finish()).catch(()=>{});
    // Detaching removes the browser's debugging bar, which makes the visible
    // area taller a moment later. The scroll method must not start before that.
    if(attached && !captured && !canceledByUser) await inject(tab,async()=>{
      const frame=()=>new Promise(resolve=>requestAnimationFrame(resolve));
      let last=innerHeight,stable=0;
      for(let i=0;i<60 && stable<6;i++){
        await frame();
        stable=innerHeight===last?stable+1:0;
        last=innerHeight;
      }
    }).catch(()=>{});
  }
}

async function fullPageScreenshot(tab, download = true, direct = false) {
  if (fullPageCaptureRunning) throw new Error("A full-page screenshot is already running.");
  fullPageCaptureRunning = true;
  try {
    const fast=await tryFastFullPageScreenshot(tab).catch(error=>{
      if(error?.name==="PowerTroveCaptureCanceled") throw error;
      return null;
    });
    const png=fast ?? await scrollFullPageScreenshot(tab);
    return download ? await downloadScreenshot(png,tab,"full-page",{direct}) : png;
  }finally{fullPageCaptureRunning=false;}
}

async function scrollFullPageScreenshot(tab) {
  let initialized = false;
  try {
    // Use the isolated extension world. Never run page-supplied scripts or
    // install a persistent listener or alter a website's storage.
    await chrome.scripting.executeScript({target:{tabId:tab.id},files:["fullpage-capture.js"]});
    initialized = true;
    const m = await inject(tab, () => globalThis.PowerTroveFullPage.begin());
    if (!m?.viewportH || !m.viewportW || !m.height ||
        m.height > 16000 || m.viewportW > 16000)
      throw new Error("Page dimensions exceed safe screenshot limits.");

    const currentTab = await chrome.tabs.get(tab.id);
    if (!currentTab || currentTab.url !== tab.url)
      throw new Error("The page changed during the screenshot.");

    let canvas, ctx, scale;
    // Each tile has an exact output pixel interval, so fractional devicePixelRatio
    // cannot leave white 1px seams between separately rounded drawImage calls.
    for (let top = 0; top < m.height; top += m.viewportH) {
      const last = top + m.viewportH >= m.height;
      const target = Math.min(top, Math.max(0, m.height - m.viewportH));
      const position = await inject(tab, async (y,first,final) =>
        globalThis.PowerTroveFullPage.move(y,first,final), [target,top===0,last]);

      if (!position || Math.abs(position.y - target) > 2 ||
          position.viewportW !== m.viewportW || position.viewportH !== m.viewportH ||
          Math.abs(position.height - m.height) > 32)
        throw new Error("Page layout changed while capturing. Try again after it settles.");

      const [active] = await chrome.tabs.query({active:true,windowId:tab.windowId});
      const latest = await chrome.tabs.get(tab.id);
      if (active?.id !== tab.id || latest?.url !== tab.url)
        throw new Error("The active page changed during the screenshot.");

      const captured = await screenshot(tab,true);
      const [afterCapture] = await chrome.tabs.query({active:true,windowId:tab.windowId});
      const afterTab = await chrome.tabs.get(tab.id);
      if (afterCapture?.id !== tab.id || afterTab?.url !== tab.url)
        throw new Error("The active page changed during the screenshot.");
      const shot = await decodedPng(captured);
      try {
        if (!canvas) {
          scale = shot.width / m.viewportW;
          const height = Math.round(m.height * scale);
          if (!Number.isFinite(scale) || scale <= 0 || height <= 0 ||
              shot.width * height > MAX_FULL_PIXELS)
            throw new Error("Full-page image exceeds the 20-megapixel limit.");
          canvas = new OffscreenCanvas(shot.width,height);
          ctx = canvas.getContext("2d");
          if (!ctx) throw new Error("Image composition is unavailable.");
        }
        if (shot.width !== canvas.width ||
            Math.abs(shot.height - m.viewportH * scale) > 3)
          throw new Error("Browser viewport changed while capturing.");

        const destinationY = Math.round(top * scale);
        const destinationEnd = Math.round(Math.min(m.height,top + m.viewportH) * scale);
        const sourceY = Math.max(0, Math.round((top - position.y) * scale));
        const slice = Math.min(destinationEnd - destinationY, shot.height - sourceY);
        if (sourceY >= shot.height || slice <= 0)
          throw new Error("Screenshot tiles could not be aligned.");
        ctx.drawImage(shot,0,sourceY,shot.width,slice,
          0,destinationY,canvas.width,slice);
      } finally {
        shot.close();
      }
    }
    if (!canvas) throw new Error("No image captured.");
    return await pngData(canvas);
  } finally {
    // Also restore scroll position and inline styles when the tab was
    // switched, closed, a capture quota error occurred or encoding failed.
    if (initialized) await inject(tab, () => globalThis.PowerTroveFullPage?.finish()).catch(()=>{});
  }
}
// Used only by the authenticated desktop on-demand capture request.
globalThis.PowerTroveCaptureFullPage = tab => fullPageScreenshot(tab,false);

async function areaScreenshot(tab) {
  const selection = await inject(tab, () => new Promise(resolve => {
    const overlay = document.createElement("div");
    const rectangle = document.createElement("div");
    overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;cursor:crosshair;background:rgba(0,0,0,.12);";
    rectangle.style.cssText = "position:absolute;border:2px solid #38bdf8;background:rgba(56,189,248,.15);pointer-events:none;";
    overlay.appendChild(rectangle);
    const selection = {fromX:0,fromY:0,toX:0,toY:0};
    let dragging = false, finished = false;
    const finish = value => {
      if (finished) return; finished=true;
      document.removeEventListener("keydown",onKey,true);
      overlay.remove();
      resolve(value);
    };
    const onKey = event => {
      if (event.key === "Escape") {event.preventDefault();finish(null);}
    };
    document.addEventListener("keydown",onKey,true);
    overlay.addEventListener("pointerdown",event=>{
      if (event.button!==0)return;
      dragging=true; overlay.setPointerCapture(event.pointerId);
      selection.fromX=event.clientX;selection.fromY=event.clientY;
      selection.toX=event.clientX;selection.toY=event.clientY;
    });
    overlay.addEventListener("pointermove",event=>{
      if (!dragging)return;
      selection.toX=event.clientX;selection.toY=event.clientY;
      const x=Math.min(selection.fromX,selection.toX),y=Math.min(selection.fromY,selection.toY);
      rectangle.style.left=x+"px";rectangle.style.top=y+"px";
      rectangle.style.width=Math.abs(selection.toX-selection.fromX)+"px";
      rectangle.style.height=Math.abs(selection.toY-selection.fromY)+"px";
    });
    overlay.addEventListener("pointerup",event=>{
      if (!dragging)return;
      const x=Math.max(0,Math.min(selection.fromX,event.clientX)),y=Math.max(0,Math.min(selection.fromY,event.clientY));
      const w=Math.abs(selection.fromX-event.clientX),h=Math.abs(selection.fromY-event.clientY);
      finish(w>=5&&h>=5 ? {x,y,width:w,height:h,viewportW:window.innerWidth,viewportH:window.innerHeight}:null);
    });
    overlay.addEventListener("contextmenu",event=>{event.preventDefault();finish(null);});
    document.documentElement.appendChild(overlay);
  }));
  if (!selection) return;
  await new Promise(resolve=>setTimeout(resolve,180));
  const image=await decodedPng(await screenshot(tab));
  const ratio=image.width/selection.viewportW;
  const x=Math.max(0,Math.floor(selection.x*ratio));
  const y=Math.max(0,Math.floor(selection.y*ratio));
  const width=Math.min(image.width-x,Math.floor(selection.width*ratio));
  const height=Math.min(image.height-y,Math.floor(selection.height*ratio));
  if (width<=0 || height<=0) {image.close();throw new Error("Invalid screenshot selection.");}
  const canvas=new OffscreenCanvas(width,height);
  canvas.getContext("2d").drawImage(image,x,y,width,height,0,0,width,height);
  image.close();
  return await downloadScreenshot(await pngData(canvas),tab,"area");
}
async function sendSelection(tab, override, pageLink = false) {
  const info = await content(tab,"pageInfo");
  const selected = typeof override === "string" && override.length ? override : info.selection;
  const text = pageLink ? info.url : selected;
  if (!text) throw new Error("Mark text on this page first.");
  if (text.length > 24576) throw new Error("Selection exceeds PowerTrove's 24,576-character limit.");
  const reply = await chrome.runtime.sendNativeMessage(HOST,{
    type:"openSelection",text,url:info.url,title:info.title.slice(0,300)
  });
  if (!reply?.ok) throw new Error("PowerTrove desktop bridge is unavailable. See the Browser Pack setup guide.");
}
async function pageImages(tab) {
  const images = (await content(tab,"images")).slice(0,25);
  if (!images.length) throw new Error("No downloadable images found.");
  for (let i=0;i<images.length;i++) {
    const image = images[i];
    let ending = "jpg";
    try {const x = new URL(image.url).pathname.match(/\.(png|jpeg|jpg|webp|gif|avif|svg)$/i);if(x)ending=x[1].toLowerCase();}
    catch{}
    const name = safeName(tab.title+"-image-"+String(i+1).padStart(2,"0"),ending);
    await downloadUrl(image.url,name,{direct:true});
  }
}
const escapeCsv = value => {
  let cell = String(value ?? "").replace(/[\x00-\x08\x0b\x0c\x0e-\x1f]/g,"");
  // Prevent spreadsheet apps treating untrusted page text as formulas.
  if (/^\s*[=+@\-\t\r]/.test(cell)) cell = "'" + cell;
  return '"' + cell.replace(/"/g,'""') + '"';
};
const asMarkdown = value => String(value ?? "").replace(/\|/g,"\\|").replace(/[\r\n]+/g," ");
async function sendLargestTable(tab, chosenIndex) {
  const tables=await content(tab,"tables");
  if(!tables.length)throw new Error("No HTML table found.");
  // A normal browser action is an explicit user gesture. Never capture all tables
  // in the background or silently truncate rows.
  const table=Number.isInteger(chosenIndex)
    ? tables.find(t=>t.index===chosenIndex)
    : [...tables].sort((a,b)=>b.rows*b.columns-a.rows*a.columns)[0];
  if(!table)throw new Error("The selected table is no longer available.");
  if(table.rows>100||table.columns>20)throw new Error("Table is too large. Export as CSV instead.");
  const info=await content(tab,"pageInfo");
  const payload={type:"openTable",url:info.url,title:info.title.slice(0,300),
    rows:table.data};
  if(new TextEncoder().encode(JSON.stringify(payload)).length>60000)
    throw new Error("Table is too large. Export as CSV instead.");
  const reply=await chrome.runtime.sendNativeMessage(HOST,payload);
  if(!reply?.ok)throw new Error("PowerTrove desktop bridge is unavailable.");
}
async function exportTables(tab, format, chosenIndex) {
  const tables = await content(tab,"tables");
  if(!tables.length)throw new Error("No tables found on this page.");
  // A popup picker selects one table. Context-menu callers use the first table.
  // Never silently download multiple unrelated tables or truncate at ten.
  const selected = Number.isInteger(chosenIndex)
    ? tables.find(table=>table.index===chosenIndex) : tables[0];
  if (!selected) throw new Error("The chosen table is no longer available.");
  for(const table of [selected]){
    const rows=table.data;
    let text,extension,mime;
    if(format==="csv"){
      text=rows.map(row=>row.map(escapeCsv).join(",")).join("\r\n")+"\r\n";
      extension="csv";mime="text/csv";
    }else if(format==="json"){
      text=JSON.stringify(rows,null,2);extension="json";mime="application/json";
    }else{
      const headings=rows[0]||[];
      text="| "+headings.map(asMarkdown).join(" | ")+" |\n| "+
        headings.map(()=>"---").join(" | ")+" |\n"+
        rows.slice(1).map(row=>"| "+row.map(asMarkdown).join(" | ")+" |").join("\n");
      extension="md";mime="text/markdown";
    }
    await downloadText(text,safeName(tab.title+"-table-"+(table.index+1),extension),mime);
  }
}
async function exportLinks(tab,format){
  const links=await content(tab,"links");
  if(!links.length)throw new Error("No links found.");
  const text=format==="csv"
    ? "Title,URL\r\n"+links.map(x=>escapeCsv(x.title)+","+escapeCsv(x.url)).join("\r\n")+"\r\n"
    : links.map(x=>"- ["+x.title.replace(/([\[\]\\])/g,"\\$1")+"]("+x.url+")").join("\n");
  await downloadText(text,safeName(tab.title+"-links",format),
    format==="csv"?"text/csv":"text/markdown");
}
async function openDownloadedId(downloadId) {
  if (!Number.isInteger(downloadId) || downloadId < 0)
    throw new Error("Download could not start.");
  // Poll only the download explicitly started by this action; no history watcher.
  for(let attempt=0;attempt<24;attempt++){
    const [record]=await chrome.downloads.search({id:downloadId});
    if(!record || record.state==="interrupted")throw new Error("Download failed.");
    if(record.state==="complete"){
      if(record.incognito || record.danger!=="safe" ||
         typeof record.filename!=="string" || !/^[A-Za-z]:\\/.test(record.filename) ||
         (Number.isFinite(record.fileSize) && record.fileSize>100*1024*1024))
        throw new Error("File cannot be opened safely in PowerTrove.");
      const reply=await chrome.runtime.sendNativeMessage(HOST,
        {type:"openDownload",path:record.filename});
      if(!reply?.ok)throw new Error("PowerTrove desktop bridge is unavailable.");
      return;
    }
    await new Promise(resolve=>setTimeout(resolve,500));
  }
  throw new Error("Download did not finish within the allowed time.");
}
async function downloadLinkedFileToPowerTrove(tab,url,isImage) {
  let parsed;
  try{parsed=new URL(url);}catch{throw new Error("Invalid file URL.");}
  if(!["https:","http:"].includes(parsed.protocol)||parsed.username||parsed.password)
    throw new Error("Only normal web file links are supported.");
  const extension=(parsed.pathname.match(/\.(pdf|docx|xlsx|csv|txt|json|md|svg|png|jpe?g|webp|gif|avif|epub|zip)$/i)||[])[1];
  if(!extension)throw new Error("This link is not a supported file type.");
  if(isImage&&!/^(svg|png|jpe?g|webp|gif|avif)$/i.test(extension))
    throw new Error("This is not a supported image.");
  const downloadId=await downloadUrl(parsed.href,
    safeName(tab.title+"-"+(isImage?"image":"file"),extension.toLowerCase()),{direct:true});
  await openDownloadedId(downloadId);
}
async function openLastDownload() {
  // Explicit user gesture only. No background download history tracking.
  const entries=await chrome.downloads.search({orderBy:["-startTime"],limit:12});
  const now=Date.now();
  const entry=entries.find(d=>d.state==="complete" && !d.incognito && d.danger==="safe" &&
    typeof d.filename==="string" && /^[a-zA-Z]:\\/.test(d.filename) &&
    (!Number.isFinite(d.fileSize) || d.fileSize <= 100*1024*1024) &&
    d.endTime && Number.isFinite(Date.parse(d.endTime)) &&
    now-Date.parse(d.endTime)>=0 && now-Date.parse(d.endTime)<=600000);
  if(!entry)throw new Error("No recent safe local download found.");
  const reply=await chrome.runtime.sendNativeMessage(HOST,{type:"openDownload",path:entry.filename});
  if(!reply?.ok)throw new Error("PowerTrove desktop bridge is unavailable.");
}
async function executeAction(action, suppliedTab, selectionText) {
  const tab = action === "openLastDownload" ? null
    : suppliedTab?.id ? suppliedTab : await activeTab();
  if (action!=="openLastDownload" && !/^https?:\/\//i.test(tab.url || ""))throw new Error("This browser page is restricted.");
  switch(action) {
    case "openLastDownload":await openLastDownload();break;
    case "sendSelection": await sendSelection(tab,selectionText,false);break;
    case "sendPageUrl": await sendSelection(tab,null,true);break;
    case "saveArticleMarkdown": await downloadText(await content(tab,"article"),safeName(tab.title+"-article","md"));break;
    case "savePageMarkdown": await downloadText(await content(tab,"markdown"),safeName(tab.title,"md"));break;
    case "saveSelectionMarkdown": {
      const text=selectionText || await content(tab,"selection");
      if(!text)throw new Error("Select text first.");
      await downloadText("> "+text.split(/\r?\n/).join("\n> "),
        safeName(tab.title+"-selection","md"));break;
    }
    case "saveMhtml":{
      const blob=await chrome.pageCapture.saveAsMHTML({tabId:tab.id});
      await downloadUrl(await bytesToDataUrl(blob,"multipart/related"),safeName(tab.title,"mhtml"));break;
    }
    case "printPdf":
      await inject(tab,()=>{window.print();return true;});break;
    case "visibleScreenshot":
      await downloadScreenshot(await screenshot(tab),tab,"visible");break;
    case "visibleScreenshotToPowerTrove":
      await openDownloadedId(await downloadScreenshot(await screenshot(tab),tab,"visible",{direct:true}));break;
    case "fullScreenshotToPowerTrove":
      await openDownloadedId(await fullPageScreenshot(tab,true,true));break;
    case "openImageFromContext":
      await downloadLinkedFileToPowerTrove(tab,selectionText,true);break;
    case "openLinkFromContext":
      await downloadLinkedFileToPowerTrove(tab,selectionText,false);break;
    case "openEmbeddedPdfInPowerTrove": {
      const url=await content(tab,"embeddedPdfUrl");
      if(typeof url!=="string")
        throw new Error("No single downloadable embedded PDF found. Use its file link instead.");
      await downloadLinkedFileToPowerTrove(tab,url,false);break;
    }
    case "openPdfInPowerTrove": {
      const current = new URL(tab.url);
      if(!/\.pdf$/i.test(current.pathname))
        throw new Error("Open a direct PDF URL in the browser first.");
      await downloadLinkedFileToPowerTrove(tab,current.href,false);break;
    }
    case "areaScreenshot":await areaScreenshot(tab);break;
    case "fullScreenshot":await fullPageScreenshot(tab);break;
    case "downloadImages":await pageImages(tab);break;
    case "sendLargestTable":await sendLargestTable(tab,selectionText);break;
    case "exportTablesCsv":await exportTables(tab,"csv",selectionText);break;
    case "exportTablesJson":await exportTables(tab,"json",selectionText);break;
    case "exportTablesMarkdown":await exportTables(tab,"md",selectionText);break;
    case "exportLinksCsv":await exportLinks(tab,"csv");break;
    case "exportLinksMarkdown":await exportLinks(tab,"md");break;
    default:throw new Error("Unsupported Browser Pack action.");
  }
}
const MENUS=[
  ["sendSelection","selection"],
  ["saveSelectionMarkdown","selection"],
  ["sendPageUrl","page"],
  ["saveArticleMarkdown","page"],
  ["savePageMarkdown","page"],
  ["saveMhtml","page"],
  ["printPdf","page"],
  ["visibleScreenshot","page"],
  ["areaScreenshot","page"],
  ["fullScreenshot","page"],
  ["downloadImages","page"],
  ["visibleScreenshotToPowerTrove","page"],
  ["fullScreenshotToPowerTrove","page"],
  ["openImageFromContext","image"],
  ["openLinkFromContext","link"],
  ["sendLargestTable","page"],
  ["exportTablesCsv","page"],
  ["exportTablesJson","page"],
  ["exportTablesMarkdown","page"],
  ["exportLinksCsv","page"],
  ["exportLinksMarkdown","page"]
];
chrome.runtime.onInstalled.addListener(async () => {
  await chrome.contextMenus.removeAll();
  for(const [id,context] of MENUS) {
    chrome.contextMenus.create({id,title:chrome.i18n.getMessage(id),contexts:[context]});
  }
});
chrome.contextMenus.onClicked.addListener((info,tab)=>{
  const selected=info.menuItemId==="openImageFromContext" ? info.srcUrl :
    info.menuItemId==="openLinkFromContext" ? info.linkUrl : info.selectionText;
  executeAction(info.menuItemId,tab,selected).catch(()=>{});
});
chrome.commands.onCommand.addListener(command=>{
  if (command==="open-selection") executeAction("sendSelection").catch(()=>{});
});
chrome.runtime.onMessage.addListener((message,sender,respond)=>{
  if(message?.type==="companionStatus") {
    respond(globalThis.PowerTroveCompanion?.status() ?? {enabled:false,connected:false});
    return false;
  }
  if(message?.type==="startCompanion") {
    if (!globalThis.PowerTroveCompanion) { respond({ok:false});return false; }
    PowerTroveCompanion.enable().then(()=>respond({ok:true})).catch(()=>respond({ok:false}));
    return true;
  }
  if(message?.type==="stopCompanion") {
    if(!globalThis.PowerTroveCompanion){respond({ok:false});return false;}
    PowerTroveCompanion.stop()
      .then(async()=>{
        // Revoke opt-in broad page permissions; activeTab popup actions still work.
        await chrome.permissions.remove({permissions:["tabs"],origins:["<all_urls>","http://*/*","https://*/*"]});
        respond({ok:true});
      }).catch(()=>respond({ok:false}));
    return true;
  }
  if(message?.type==="tableSummaries") {
    activeTab().then(tab=>content(tab,"tablesMeta"))
      .then(tables=>respond({ok:true,tables}))
      .catch(()=>respond({ok:false,tables:[]}));
    return true;
  }
  if(message?.type!=="run" || typeof message.action!=="string")return false;
  executeAction(message.action,undefined,message.tableIndex).then(()=>respond({ok:true})).catch(error=>
    respond({ok:false,error:String(error?.message||"Action failed.").slice(0,250)}));
  return true;
});

// Chrome stops and restarts the service worker at any time, not only on browser start or
// install. Restore an enabled session on every start; load() does nothing while connected.
void globalThis.PowerTroveCompanion?.load();
// Chrome starts a worker at browser launch only when it listens for onStartup. The listener
// itself has nothing to do: waking the worker runs the load() above, also after a PC restart.
chrome.runtime.onStartup.addListener(() => {});
