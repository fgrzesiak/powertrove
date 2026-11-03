const translate = name => chrome.i18n.getMessage(name) || name;
document.documentElement.lang = chrome.i18n.getUILanguage().split("-")[0];
document.querySelectorAll("[data-i18n]").forEach(el=>{el.textContent=translate(el.dataset.i18n);});
document.querySelectorAll("[data-placeholder]").forEach(el=>{el.placeholder=translate(el.dataset.placeholder);});
document.querySelectorAll("[data-aria-label]").forEach(el=>{el.setAttribute("aria-label",translate(el.dataset.ariaLabel));});
document.querySelectorAll("button[data-action][data-icon]").forEach(el=>{
  const icon=document.createElement("span");icon.className="glyph";icon.setAttribute("aria-hidden","true");
  icon.innerHTML=el.dataset.icon;el.prepend(icon);
});
async function chooseTable(forExport = false) {
  const response=await chrome.runtime.sendMessage({type:"tableSummaries"});
  const tables=Array.isArray(response?.tables)?response.tables:[];
  if(!response?.ok||!tables.length)throw new Error(translate("noTables"));
  if(tables.length===1)return tables[0].index;
  const dialog=document.getElementById("tablePicker");
  const select=document.getElementById("tablePickerSelect");
  select.replaceChildren();
  for(const table of tables){
    const option=document.createElement("option");
    option.value=String(table.index);
    option.textContent=translate("tablePickerItem")
      .replace("{0}",String(table.index+1))
      .replace("{1}",String(table.rows))
      .replace("{2}",String(table.columns));
    select.appendChild(option);
  }
  document.getElementById("tablePickerConfirm").textContent=translate(
    forExport?"tablePickerExport":"tablePickerConfirm");
  dialog.returnValue="cancel";
  dialog.showModal();
  const choice=await new Promise(resolve=>dialog.addEventListener("close",
    ()=>resolve(dialog.returnValue),{once:true}));
  if(choice!=="confirm")return null;
  return Number(select.value);
}
const status=document.getElementById("status");
const search=document.getElementById("actionSearch");
const connection=document.getElementById("connection");
const connectCard=document.getElementById("connectCard");
const disconnectCard=document.getElementById("disconnectCard");
const accessCard=document.getElementById("accessCard");
const fastCaptureSwitch=document.getElementById("fastFullPageScreenshots");
// Browser-only exports do not require the desktop companion. Default to
// the native Save As picker; switching to Downloads is a deliberate choice.
const saveMode=document.getElementById("saveMode");
async function refreshSaveMode(){
  const saved=await chrome.storage.local.get({askWhereToSave:true});
  saveMode.value=saved.askWhereToSave===false?"downloads":"ask";
}
saveMode.addEventListener("change",async()=>{
  saveMode.disabled=true;
  status.classList.remove("error");
  try {
    await chrome.storage.local.set({askWhereToSave:saveMode.value!=="downloads"});
    status.textContent=translate("saveModeChanged");
  }catch(error) {
    status.classList.add("error");
    status.textContent=translate("saveModeFailure");
    await refreshSaveMode().catch(()=>{saveMode.value="ask";});
  }finally{saveMode.disabled=false;}
});

async function refreshFastCaptureSetting(){
  const saved=await chrome.storage.local.get({fastFullPageScreenshots:false});
  const granted=await chrome.permissions.contains({permissions:["debugger"]});
  fastCaptureSwitch.checked=Boolean(saved.fastFullPageScreenshots && granted);
  // Enterprise restrictions can prevent an otherwise required permission from working.
  if(saved.fastFullPageScreenshots && !granted)
    await chrome.storage.local.set({fastFullPageScreenshots:false});
}
fastCaptureSwitch.addEventListener("change",async()=>{
  const enabling=fastCaptureSwitch.checked;
  fastCaptureSwitch.disabled=true;
  status.classList.remove("error");
  try {
    if(enabling){
      // Chrome disallows debugger in optional_permissions. Installation grants
      // the required permission; the switch controls use, not installation consent.
      const granted=await chrome.permissions.contains({permissions:["debugger"]});
      if(!granted){
        fastCaptureSwitch.checked=false;
        status.classList.add("error");
        status.textContent=translate("fastFullPageDenied");
      }else{
        await chrome.storage.local.set({fastFullPageScreenshots:true});
        status.textContent=translate("fastFullPageEnabled");
      }
    }else{
      // Required extension permissions cannot be revoked individually.
      // Disabling the switch guarantees no debugger.attach invocation.
      await chrome.storage.local.set({fastFullPageScreenshots:false});
      status.textContent=translate("fastFullPageDisabled");
    }
  }catch(error){
    status.classList.add("error");
    status.textContent=error?.message||translate("failed");
    if(enabling){
      await chrome.storage.local.set({fastFullPageScreenshots:false}).catch(()=>{});
      fastCaptureSwitch.checked=false;
    }
  }finally{
    await refreshFastCaptureSetting().catch(()=>{fastCaptureSwitch.checked=false;});
    fastCaptureSwitch.disabled=false;
  }
});
let chosen=0;
const actions=()=>[...document.querySelectorAll("button[data-action]")].filter(b=>
  !b.hidden && !b.closest("[hidden]") && !["connectDesktop","disconnectDesktop"].includes(b.dataset.action));
function filterActions(){
  const query=search.value.trim().toLocaleLowerCase();
  for(const button of document.querySelectorAll("button[data-action]")){
    if(["connectDesktop","disconnectDesktop"].includes(button.dataset.action))continue;
    button.hidden=!!query && !button.textContent.toLocaleLowerCase().includes(query);
  }
  document.querySelectorAll("section").forEach(section=>{
    section.hidden=![...section.querySelectorAll("button[data-action]")].some(b=>!b.hidden);
  });
  chosen=0;highlight();
}
function highlight(){
  actions().forEach((el,i)=>el.classList.toggle("keyboard-selected",i===chosen));
}
search.addEventListener("input",filterActions);
search.addEventListener("keydown",e=>{
  const visible=actions();
  if(!visible.length)return;
  if(e.key==="ArrowDown"||e.key==="ArrowUp"){
    e.preventDefault();chosen=(chosen+(e.key==="ArrowDown"?1:-1)+visible.length)%visible.length;highlight();
    visible[chosen].scrollIntoView({block:"nearest"});
  }else if(e.key==="Enter"){e.preventDefault();visible[chosen]?.click();}
  else if(e.key==="Escape"){search.value="";filterActions();}
});
async function refreshConnection(){
  try{
    const r=await chrome.runtime.sendMessage({type:"companionStatus"});
    connection.textContent=translate(r?.connected?"connectionConnected":
      r?.enabled?"connectionConfigured":"connectionDisconnected");
    connection.className=r?.connected?"connected":r?.enabled?"configured":"";
    connectCard.hidden=!!r?.connected;
    disconnectCard.hidden=!r?.enabled;
    // Connections made before screenshots from PowerTrove existed only allowed http(s) pages.
    accessCard.hidden=!r?.enabled||await chrome.permissions.contains({origins:["<all_urls>"]});
  }catch{
    connection.textContent=translate("connectionDisconnected");
    connectCard.hidden=false;
    disconnectCard.hidden=true;
    accessCard.hidden=true;
  }
}
document.addEventListener("click",async event=>{
  const button=event.target.closest("button[data-action]");
  if(!button)return;
  const action=button.dataset.action;
  status.classList.remove("error");status.textContent=translate("working");
  button.disabled=true;
  try{
    if(action==="connectDesktop"){
      const browser=/Edg\//.test(navigator.userAgent)?"edge":"chrome";
      try{await chrome.permissions.request({permissions:["tabs"],origins:["<all_urls>"]});}
      catch{/* Limited mode does not access page content. */}
      const started=await chrome.runtime.sendMessage({type:"startCompanion"});
      if(!started?.ok)throw Error(translate("failed"));
      await chrome.tabs.create({url:`powertrove://connect?browser=${browser}&id=${chrome.runtime.id}`});
      status.textContent=translate("connectOpened");
      await refreshConnection();return;
    }
    if(action==="allowAllPages"){
      if(!await chrome.permissions.request({origins:["<all_urls>"]}))throw Error(translate("failed"));
      status.textContent=translate("done");
      await refreshConnection();return;
    }
    if(action==="disconnectDesktop"){
      const response=await chrome.runtime.sendMessage({type:"stopCompanion"});
      if(!response?.ok)throw Error(translate("failed"));
      status.textContent=translate("disconnectedDone");
      await refreshConnection();return;
    }
    if(action==="copyLinkMarkdown"){
      const [tab]=await chrome.tabs.query({active:true,currentWindow:true});
      if(!tab?.url||!/^https?:\/\//i.test(tab.url))throw Error(translate("failed"));
      const title=(tab.title||tab.url).replace(/\\/g,"\\\\").replace(/\[/g,"\\[").replace(/\]/g,"\\]");
      await navigator.clipboard.writeText("["+title+"]("+tab.url+")");
      status.textContent=translate("done");return;
    }
    let tableIndex;
    if(action==="sendLargestTable"||["exportTablesCsv","exportTablesJson","exportTablesMarkdown"].includes(action)){
      tableIndex=await chooseTable(action!=="sendLargestTable");
      if(tableIndex===null){status.textContent="";return;}
    }
    const result=await chrome.runtime.sendMessage({type:"run",action,tableIndex});
    if(!result?.ok)throw Error(result?.error||translate("failed"));
    status.textContent=translate("done");
  }catch(error){status.classList.add("error");status.textContent=error?.message||translate("failed");}
  finally{button.disabled=false;}
});
void refreshConnection();
void refreshFastCaptureSetting();
void refreshSaveMode().catch(()=>{saveMode.value="ask";});
filterActions();
search.focus();
