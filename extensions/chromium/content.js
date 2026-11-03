(() => {
  if (globalThis.__powerTroveBrowserContentV1) return;
  globalThis.__powerTroveBrowserContentV1 = true;
  const MAX_CHARS = 250000;
  const normalize = text => (text || "").replace(/\s+/g, " ").trim();
  const safeUrl = value => {
    try { const u = new URL(value, document.baseURI); return /^https?:$/.test(u.protocol) ? u.href : ""; }
    catch { return ""; }
  };
  const title = () => normalize(document.title) || "Web page";
  const strip = text => (text || "").replace(/\\/g, "\\\\").replace(/([*_\[\]`])/g, "\\$1");
  const inline = (node, depth = 0) => {
    if (depth > 24) return "";
    if (node.nodeType === Node.TEXT_NODE) return (node.nodeValue || "").replace(/\s+/g, " ");
    if (node.nodeType !== Node.ELEMENT_NODE) return "";
    const name = node.tagName.toLowerCase();
    if (["script", "style", "noscript", "svg", "iframe", "button", "form"].includes(name)) return "";
    if (name === "br") return "\n";
    if (name === "img") {
      const src = safeUrl(node.currentSrc || node.getAttribute("src"));
      return src ? `![${strip(node.getAttribute("alt") || "")}](${src})` : "";
    }
    const text = [...node.childNodes].map(child => inline(child, depth + 1)).join("");
    if (name === "a") {
      const href = safeUrl(node.getAttribute("href"));
      return href && text.trim() ? `[${text.trim()}](${href})` : text;
    }
    if (name === "code") return `\`${text.replace(/\`/g, "\\`")}\``;
    if (name === "strong" || name === "b") return `**${text.trim()}**`;
    if (name === "em" || name === "i") return `*${text.trim()}*`;
    return text;
  };
  const blocks = (root, depth = 0) => {
    if (depth > 22) return "";
    const output = [];
    for (const child of root.children || []) {
      const tag = child.tagName.toLowerCase();
      if (["script", "style", "nav", "footer", "aside", "iframe", "form", "noscript"].includes(tag)) continue;
      if (child.getAttribute("aria-hidden") === "true" || child.hidden) continue;
      if (/^h[1-6]$/.test(tag)) output.push("#".repeat(Number(tag[1])) + " " + inline(child).trim());
      else if (tag === "p" || tag === "blockquote") {
        const paragraph = inline(child).trim();
        if (paragraph) output.push(tag === "blockquote" ? "> " + paragraph.replace(/\n/g, "\n> ") : paragraph);
      } else if (tag === "pre") {
        const raw = child.textContent || "";
        const fence = raw.includes("```") ? "````" : "```";
        output.push(fence + "\n" + raw.trimEnd().slice(0, 20000) + "\n" + fence);
      } else if (tag === "ul" || tag === "ol") {
        const items = [...child.children].filter(e => e.matches("li")).slice(0, 250);
        output.push(items.map((li, i) => (tag === "ol" ? (i + 1) + ". " : "- ") + inline(li).trim()).join("\n"));
      } else if (tag === "table") {
        const rows = [...child.querySelectorAll("tr")].slice(0, 150).map(tr =>
          [...tr.querySelectorAll(":scope > td, :scope > th")].slice(0, 16)
            .map(td => normalize(td.textContent).replace(/\|/g, "\\|")));
        if (rows.length && rows[0].length) {
          const width = rows[0].length;
          output.push("| " + rows[0].join(" | ") + " |\n| " +
            Array(width).fill("---").join(" | ") + " |\n" +
            rows.slice(1).map(row => "| " + row.slice(0, width).join(" | ") + " |").join("\n"));
        }
      } else if (tag === "img" || tag === "figure") {
        const result = inline(child).trim();
        if (result) output.push(result);
      } else if (tag === "hr") output.push("---");
      else if (["div", "section", "article", "main", "header"].includes(tag)) {
        const content = blocks(child, depth + 1);
        if (content) output.push(content);
      } else {
        const content = inline(child).trim();
        if (content && tag !== "input") output.push(content);
      }
      if (output.join("\n\n").length > MAX_CHARS) break;
    }
    return output.filter(Boolean).join("\n\n");
  };
  const extractMarkdown = articleOnly => {
    const root = articleOnly ?
      (document.querySelector("article") || document.querySelector("main") ||
       document.querySelector('[role="main"]') || document.body) : document.body;
    const header = `# ${strip(title())}\n\nSource: ${safeUrl(location.href)}\n\n`;
    return (header + blocks(root)).slice(0, MAX_CHARS);
  };
  const images = () => [...document.images].slice(0, 500)
    .map(img => ({url: safeUrl(img.currentSrc || img.src), alt: normalize(img.alt).slice(0, 120),
      width: img.naturalWidth, height: img.naturalHeight}))
    .filter(item => item.url && item.width > 0 && item.height > 0)
    .filter((item, index, list) => list.findIndex(other => other.url === item.url) === index);
  // Explicit page-data extraction only. Never read input values or content
  // from forms, hidden tables or password containers.
  const visibleTables = () => [...document.querySelectorAll("table")].filter(t =>
    !t.closest("form") && !t.hidden && !t.querySelector('input[type="password"]'))
    .slice(0,30);
  const tableMetadata = () => visibleTables().map((table,index) => {
    const rows=Math.min(5000,table.rows.length);
    const columns=Math.min(200,Math.max(0,...[...table.rows].slice(0,20)
      .map(row=>row.cells.length)));
    return {index,rows,columns};
  });
  const extractTables = () => visibleTables()
    .slice(0, 30).map((table,index) => {
      const grid=[], covered=new Map();
      const rows=[...table.rows].slice(0,5000);
      for (let r=0;r<rows.length;r++) {
        const row=[], cells=[...rows[r].cells].slice(0,200);
        let c=0;
        for(const cell of cells){
          while(covered.get(r+":"+c)) {row[c]="";c++;}
          if(c>=200)break;
          const value=normalize(cell.textContent||"").slice(0,4096);
          row[c]=value;
          const rs=Math.max(1,Math.min(5000,cell.rowSpan||1));
          const cs=Math.max(1,Math.min(200,cell.colSpan||1));
          for(let y=r;y<Math.min(rows.length,r+rs);y++){
            for(let x=c;x<Math.min(200,c+cs);x++){
              if(y!==r||x!==c)covered.set(y+":"+x,true);
            }
          }
          c+=cs;
        }
        grid.push(row.slice(0,200));
      }
      const width=Math.min(200,Math.max(0,...grid.map(row=>row.length)));
      const data=grid.map(row=>Array.from({length:width},(_,i)=>row[i]||""));
      return {index,rows:data.length,columns:width,data};
    }).filter(t=>t.rows>0 && t.columns>0);
  const extractLinks = () => [...document.querySelectorAll('a[href]')].slice(0,1000)
    .filter(a=>!a.closest('form') && !a.closest('[aria-hidden="true"]'))
    .map(a=>({title:normalize(a.textContent||"").slice(0,150),
      url:safeUrl(a.getAttribute("href"))}))
    .filter(a=>a.url).slice(0,200);
  // Explicit support for an HTML page embedding an HTTP(S) PDF.
  // Never inspect a browser-internal PDF viewer, data/blob URLs, or hidden forms.
  const embeddedPdfUrl = () => {
    const found = new Set();
    const elements = [...document.querySelectorAll(
      'embed[src], object[data], iframe[src], a[href]')].slice(0,500);
    for(const node of elements) {
      if(node.closest('form,[aria-hidden="true"]') || node.hidden)continue;
      const raw=node.getAttribute(node.tagName.toLowerCase()==="object"?"data":
        node.tagName.toLowerCase()==="a"?"href":"src");
      const address=safeUrl(raw);
      if(!address)continue;
      const type=(node.getAttribute("type")||"").toLowerCase();
      const path=(()=>{try{return new URL(address).pathname;}catch{return "";}})();
      if((type==="application/pdf" || /\\.pdf$/i.test(path)) &&
          /\\.pdf$/i.test(path)) found.add(address);
    }
    // More than one PDF is ambiguous: require an explicit source link action.
    return found.size===1?[...found][0]:null;
  };
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message?.target !== "powertrove-content-v1") return false;
    try {
      let result;
      switch (message.action) {
        case "markdown": result = extractMarkdown(false); break;
        case "article": result = extractMarkdown(true); break;
        case "selection": result = (getSelection()?.toString() || "").slice(0, MAX_CHARS); break;
        case "images": result = images(); break;
        case "tables": result = extractTables(); break;
        case "tablesMeta": result = tableMetadata(); break;
        case "links": result = extractLinks(); break;
        case "embeddedPdfUrl": result = embeddedPdfUrl(); break;
        case "pageInfo": result = {title: title(), url: safeUrl(location.href),
          selection: (getSelection()?.toString() || "").slice(0, 24576)}; break;
        default: throw new Error("Unsupported action");
      }
      sendResponse({ok: true, result});
    } catch {
      sendResponse({ok: false, error: "Page content is unavailable"});
    }
    return false;
  });
})();
