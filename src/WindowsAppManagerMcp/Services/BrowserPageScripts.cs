using System.Text.Json;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The scripts the DOM bridge tools run in a page (roadmap M5.8). What the agent passes (a selector, a text, an index, the
/// replace flag) goes in as JSON literals, so it is only ever data; nothing the agent passes is run as code here (only
/// browser_evaluate runs the agent's own expression, and it does not use these).
/// </summary>
public static class BrowserPageScripts
{
    /// <summary>The most elements the text search looks at, so a huge page cannot hold the call up.</summary>
    public const int MaxTextSearchElements = 50_000;

    /// <summary>
    /// The script that finds the elements a selector or a text names, run with returnByValue false: an object whose own
    /// properties are <c>count</c> (the matches), and either <c>el</c> (the element <see cref="BrowserElementLocator.Index"/>
    /// picks, or the only one), <c>first</c> (when several match and no index was given: the first five, described), or
    /// <c>error</c> (a selector that is not valid CSS). The text search takes the innermost shown elements whose text
    /// (innerText, or a button input's value) contains the text, ignoring case and runs of whitespace, and only the exact
    /// matches when there are any.
    /// </summary>
    public static string BuildFindExpression(BrowserElementLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var selector = locator.Selector == null ? "null" : JsonSerializer.Serialize(locator.Selector);
        var text = locator.Text == null ? "null" : JsonSerializer.Serialize(locator.Text);
        var index = locator.Index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        return "(() => { " +
               $"const selector = {selector}; const text = {text}; const index = {index}; " +
               "const norm = s => String(s == null ? '' : s).replace(/\\s+/g, ' ').trim().toLowerCase(); " +
               "const textOf = e => (e instanceof HTMLInputElement && /^(button|submit|reset)$/i.test(e.type)) ? norm(e.value) : norm(e.innerText); " +
               "const shown = e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden'; " +
               "const describe = e => { const t = textOf(e); return e.tagName.toLowerCase() + (t ? \" '\" + t.slice(0, 40) + \"'\" : ''); }; " +
               "let all; " +
               "if (selector !== null) { " +
               "try { all = Array.from(document.querySelectorAll(selector)); } " +
               "catch (x) { return { error: String((x && x.message) || x) || 'invalid selector' }; } " +
               "} else { " +
               "const want = norm(text); const hits = []; " +
               "const els = document.body ? document.body.getElementsByTagName('*') : []; " +
               $"const limit = Math.min(els.length, {MaxTextSearchElements}); " +
               "for (let i = 0; i < limit; i++) { const e = els[i]; " +
               "if (/^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE)$/.test(e.tagName)) continue; " +
               "if (norm(e.textContent).indexOf(want) < 0 && !(e instanceof HTMLInputElement)) continue; " +
               "if (textOf(e).indexOf(want) >= 0 && shown(e)) hits.push(e); } " +
               "const outer = new Set(); " +
               "for (const e of hits) { for (let p = e.parentElement; p; p = p.parentElement) outer.add(p); } " +
               "const inner = hits.filter(e => !outer.has(e)); " +
               "const exact = inner.filter(e => textOf(e) === want); " +
               "all = exact.length > 0 ? exact : inner; } " +
               "const count = all.length; " +
               "if (count === 0) return { count: 0 }; " +
               "if (index === null && count > 1) return { count, first: all.slice(0, 5).map(describe).join('; ') }; " +
               "if (index !== null && index >= count) return { count }; " +
               "return { count, el: all[index === null ? 0 : index] }; })()";
    }

    /// <summary>
    /// The function (Runtime.callFunctionOn, on the element) that describes an element: its tag, the start of its text, and
    /// whether it is disabled: the form control it is or is inside matches :disabled (its own disabled attribute, or a
    /// disabled fieldset's), or it or an ancestor has aria-disabled="true". The text search finds the innermost element
    /// with the text, such as a span inside a disabled button, so the ancestors count. The function runs in the element's
    /// own frame, so it reads nothing of the page's viewport (<see cref="ViewportExpression"/> does, in the main frame).
    /// </summary>
    public const string ElementStateFunction =
        "function () { " +
        "const e = this.nodeType === 1 ? this : this.parentElement; " +
        "const norm = s => String(s == null ? '' : s).replace(/\\s+/g, ' ').trim(); " +
        "if (!e) return { tag: String(this.nodeName || '').toLowerCase(), text: '', disabled: false }; " +
        "const t = (e instanceof HTMLInputElement && /^(button|submit|reset)$/i.test(e.type)) ? norm(e.value) : norm(e.innerText); " +
        "const control = e.closest('button, input, select, textarea, optgroup, option'); " +
        "return { tag: String(e.tagName || e.nodeName || '').toLowerCase(), text: t.slice(0, 80), " +
        "disabled: !!(control && control.matches(':disabled')) || !!e.closest('[aria-disabled=\"true\"]') }; }";

    /// <summary>
    /// The function (Runtime.callFunctionOn, on the element, with the point in CSS pixels of the main frame's viewport, as
    /// DOM.getContentQuads gives it) that tells whether a click at the point reaches the element: the element at the point
    /// is it, inside it, or a label of it; else what covers it. An element inside frames is checked frame by frame, from
    /// the main document down: the point must reach each frame's element, and is moved into the frame's content box for
    /// the next. A frame whose parent is of another origin cannot be followed, so such an element is refused (frame: true).
    /// The function runs in the element's own frame, whose document is not the main one for an element inside a frame.
    /// </summary>
    public const string HitTestFunction =
        "function (x, y) { " +
        "const self = this.nodeType === 1 ? this : this.parentElement; " +
        "if (!self || !self.ownerDocument) return { hit: false, by: 'nothing (the element left the page)' }; " +
        "const say = h => { const t = String(h.innerText || '').replace(/\\s+/g, ' ').trim(); " +
        "return h.tagName.toLowerCase() + (h.id ? '#' + h.id : '') + (t ? \" '\" + t.slice(0, 40) + \"'\" : ''); }; " +
        "const at = (n, px, py) => { const r = n.getRootNode && n.getRootNode().elementFromPoint ? n.getRootNode() : n.ownerDocument; " +
        "return r.elementFromPoint(px, py); }; " +
        "const frames = []; " +
        "for (let w = self.ownerDocument.defaultView; w && w !== w.top; ) { " +
        "let f = null; try { f = w.frameElement; } catch (x) { f = null; } " +
        "if (!f) return { hit: false, frame: true }; " +
        "frames.unshift(f); w = f.ownerDocument.defaultView; } " +
        "let px = x, py = y; " +
        "for (const f of frames) { " +
        "const h = at(f, px, py); " +
        "if (h !== f) return { hit: false, by: h ? say(h) : 'nothing (outside the page)' }; " +
        "const r = f.getBoundingClientRect(); const s = f.ownerDocument.defaultView.getComputedStyle(f); " +
        "px -= r.left + f.clientLeft + (parseFloat(s.paddingLeft) || 0); py -= r.top + f.clientTop + (parseFloat(s.paddingTop) || 0); } " +
        "const h = at(self, px, py); " +
        "if (!h) return { hit: false, by: 'nothing (outside the page)' }; " +
        "if (h === self || self.contains(h) || h.control === self) return { hit: true }; " +
        "return { hit: false, by: say(h) }; }";

    /// <summary>
    /// The function (Runtime.callFunctionOn, on the element browser_type focused) that tells whether the focus is on it or
    /// inside it (through shadow roots and frames: a frame's element holds the focus of what is inside it), and the tag
    /// of what has it. A page's focus handler may move the focus elsewhere, and the text must not follow it.
    /// </summary>
    public const string FocusHolderFunction =
        "function () { " +
        "const d = this.ownerDocument || document; let a = d.activeElement; " +
        "while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement; " +
        "for (let n = a; n; n = n.parentNode || n.host) { if (n === this) return { holds: true }; } " +
        "return { holds: false, tag: a && a.tagName ? a.tagName.toLowerCase() : null }; }";

    /// <summary>
    /// The script that checks the page's focused element can take typed text (a text input, a textarea or an editable
    /// element; a frame is taken on trust) and, with <paramref name="replace"/>, selects its content so the text replaces
    /// it. Returns editable, the tag and the content's length.
    /// </summary>
    public static string BuildFocusedFieldExpression(bool replace) =>
        "(() => { " +
        $"const replace = {(replace ? "true" : "false")}; " +
        "let a = document.activeElement; " +
        "while (a && a.shadowRoot && a.shadowRoot.activeElement) a = a.shadowRoot.activeElement; " +
        "if (!a || a === document.body || a === document.documentElement) return { editable: false }; " +
        "const tag = a.tagName.toLowerCase(); " +
        "if (tag === 'iframe') return { editable: true, tag, frame: true }; " +
        "const nonText = /^(button|submit|reset|checkbox|radio|file|image|range|color|hidden)$/i; " +
        "const field = tag === 'textarea' || (tag === 'input' && !nonText.test(a.type)); " +
        "if (!(field || a.isContentEditable) || a.disabled || a.readOnly) return { editable: false, tag }; " +
        "if (replace) { if (field) { a.select(); } else { const r = document.createRange(); r.selectNodeContents(a); " +
        "const s = getSelection(); s.removeAllRanges(); s.addRange(r); } } " +
        "return { editable: true, tag, length: field ? String(a.value).length : String(a.textContent).length }; })()";

    /// <summary>
    /// The script that reads the page around an element, run in the tab's main frame (Runtime.evaluate without a context):
    /// whether it is shown (visibilityState), its viewport in CSS pixels, devicePixelRatio and pinch-zoom scale, which
    /// DOM.getContentQuads' coordinates are in also for an element inside a frame, and its URL and title.
    /// </summary>
    public const string ViewportExpression =
        "({ visibility: document.visibilityState, vw: window.innerWidth, vh: window.innerHeight, dpr: window.devicePixelRatio, " +
        "scale: window.visualViewport ? window.visualViewport.scale : 1, url: location.href, title: document.title })";
}
