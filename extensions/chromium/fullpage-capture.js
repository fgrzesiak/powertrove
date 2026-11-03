// Temporary, isolated-world page state for one user-initiated full-page capture.
// Content is never read or transmitted; only dimensions and scroll coordinates leave the tab.
(() => {
  "use strict";
  // A previous interrupted capture can leave only this isolated state behind.
  try { globalThis.PowerTroveFullPage?.finish(); } catch {}

  let state = null;
  const property = (element, name, value) => {
    const original = element.style.getPropertyValue(name);
    const priority = element.style.getPropertyPriority(name);
    element.style.setProperty(name, value, "important");
    state.undo.push(() => {
      if (original) element.style.setProperty(name, original, priority);
      else element.style.removeProperty(name);
    });
  };
  const pageHeight = () => Math.max(
    document.documentElement.scrollHeight,
    document.body?.scrollHeight || 0
  );
  const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
  function trackFixed(node) {
    if (!state || state.knownFixed.has(node)) return;
    const computed = window.getComputedStyle(node);
    if (computed.position !== "fixed" || computed.visibility !== "visible") return;
    const bounds = node.getBoundingClientRect();
    if (!bounds.width || !bounds.height) return;
    const placement = bounds.top + bounds.height / 2 < window.innerHeight / 2
      ? "first" : "last";
    const original = node.style.getPropertyValue("visibility");
    const priority = node.style.getPropertyPriority("visibility");
    state.knownFixed.add(node);
    state.fixed.push({node,placement});
    state.undo.push(() => {
      if (original) node.style.setProperty("visibility",original,priority);
      else node.style.removeProperty("visibility");
    });
  }
  function findNewFloatingElements() {
    if (typeof document.elementsFromPoint !== "function") return;
    for (const x of [.15,.5,.85]) {
      for (const y of [2,32,window.innerHeight-2]) {
        for (const element of document.elementsFromPoint(
          Math.round(window.innerWidth*x), Math.max(0,y))) {
          for (let node=element;node && node!==document.documentElement;node=node.parentElement)
            trackFixed(node);
        }
      }
    }
  }

  function finish() {
    if (!state) return;
    const previous = state;
    // Restore while smooth scrolling is still disabled.
    try { window.scrollTo(previous.x, previous.y); } catch {}
    for (const undo of previous.undo.reverse()) {
      try { undo(); } catch {}
    }
    state = null;
  }

  function begin() {
    finish();
    state = {x:window.scrollX,y:window.scrollY,undo:[],fixed:[],knownFixed:new WeakSet()};
    try {
      // A page's smooth scroll/snap settings must not turn a tile into an
      // intermediate animation frame. Never permanently change page styles.
      for (const node of [document.documentElement, document.body].filter(Boolean)) {
        property(node, "scroll-behavior", "auto");
        property(node, "scroll-snap-type", "none");
      }
      const stylesheet = document.createElement("style");
      stylesheet.textContent =
        "*,*::before,*::after{animation-play-state:paused!important;" +
        "transition-duration:0s!important;caret-color:transparent!important}";
      document.documentElement.appendChild(stylesheet);
      state.undo.push(() => stylesheet.remove());

      // Sticky elements belong at their original document position exactly
      // once. Disabling stickiness keeps them in flow instead of duplicating
      // the header at every capture seam.
      for (const node of document.querySelectorAll("*")) {
        const style = window.getComputedStyle(node);
        if (style.position === "sticky") {
          property(node, "position", "static");
        } else if (style.position === "fixed") {
          trackFixed(node);
        }
      }
      return {
        height:pageHeight(), viewportW:window.innerWidth, viewportH:window.innerHeight,
        scrollX:state.x, scrollY:state.y
      };
    } catch (error) {
      finish();
      throw error;
    }
  }

  async function move(top, isFirst, isLast) {
    if (!state) throw new Error("Capture session expired.");
    const bottom = Math.max(0, pageHeight() - window.innerHeight);
    const target = Math.min(Math.max(0, top), bottom);
    window.scrollTo(0, target);
    await frame();
    // Some sites create their sticky header only after scrolling. Inspect just
    // the screen edges (not the entire document) on each tile to keep it fast.
    findNewFloatingElements();
    for (const {node,placement} of state.fixed) {
      const visible = placement === "first" ? isFirst : isLast;
      node.style.setProperty("visibility", visible ? "visible" : "hidden", "important");
    }
    await frame();
    // Only wait for newly visible, unfinished images. A bounded decode grace
    // avoids both the old fixed 530 ms sleep and obvious blank lazy images.
    const waiting = Array.from(document.images || []).filter(image => {
      if (image.complete) return false;
      const rect = image.getBoundingClientRect();
      return rect.bottom > 0 && rect.top < window.innerHeight &&
        rect.right > 0 && rect.left < window.innerWidth;
    }).slice(0,8);
    if (waiting.length)
      await Promise.race([
        Promise.all(waiting.map(image => image.decode?.().catch(()=>{}))),
        new Promise(resolve => setTimeout(resolve,180))
      ]);
    return {
      y:window.scrollY, x:window.scrollX,
      height:pageHeight(), viewportW:window.innerWidth, viewportH:window.innerHeight
    };
  }

  // Fast mode: wait until the page has laid out and painted at a new viewport
  // size, then report its height so a page that grows with the viewport is noticed.
  async function settle(ms) {
    if (!state) throw new Error("Capture session expired.");
    await frame();
    await frame();
    await new Promise(resolve => setTimeout(resolve, ms));
    return pageHeight();
  }

  globalThis.PowerTroveFullPage = {begin,move,finish,settle};
})();
