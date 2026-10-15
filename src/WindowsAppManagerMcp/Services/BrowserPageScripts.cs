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
    /// The function (Runtime.callFunctionOn, on the element) that describes an element and the page around it: its tag, the
    /// start of its text, whether it is disabled, whether it is in the page's main document, and the page's
    /// devicePixelRatio, viewport size in CSS pixels, pinch-zoom scale and visibilityState.
    /// </summary>
    public const string ElementStateFunction =
        "function () { " +
        "const e = this; const norm = s => String(s == null ? '' : s).replace(/\\s+/g, ' ').trim(); " +
        "const t = (e instanceof HTMLInputElement && /^(button|submit|reset)$/i.test(e.type)) ? norm(e.value) : norm(e.innerText); " +
        "const vv = window.visualViewport; " +
        "return { tag: String(e.tagName || e.nodeName || '').toLowerCase(), text: t.slice(0, 80), " +
        "disabled: !!e.disabled || (e.getAttribute && e.getAttribute('aria-disabled') === 'true'), " +
        "top: e.ownerDocument === document, dpr: window.devicePixelRatio, vw: window.innerWidth, vh: window.innerHeight, " +
        "scale: vv ? vv.scale : 1, visibility: document.visibilityState }; }";

    /// <summary>
    /// The function (Runtime.callFunctionOn, on the element, with the point in CSS pixels) that tells whether a click at
    /// the point reaches the element: the element at the point is it, inside it, or a label of it; else what covers it.
    /// An element inside a frame is not checked (the point is the main frame's).
    /// </summary>
    public const string HitTestFunction =
        "function (x, y) { " +
        "if (this.ownerDocument !== document) return { hit: true }; " +
        "const root = this.getRootNode && this.getRootNode().elementFromPoint ? this.getRootNode() : document; " +
        "const h = root.elementFromPoint(x, y); " +
        "if (!h) return { hit: false, by: 'nothing (outside the page)' }; " +
        "const self = this.nodeType === 1 ? this : this.parentElement; " +
        "if (self && (h === self || self.contains(h) || h.control === self)) return { hit: true }; " +
        "const t = String(h.innerText || '').replace(/\\s+/g, ' ').trim(); " +
        "return { hit: false, by: h.tagName.toLowerCase() + (h.id ? '#' + h.id : '') + (t ? \" '\" + t.slice(0, 40) + \"'\" : '') }; }";

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

    /// <summary>The script that reads what browser_screenshot needs of the page: whether it is shown, its viewport, URL and title.</summary>
    public const string ViewportExpression =
        "({ visibility: document.visibilityState, vw: window.innerWidth, vh: window.innerHeight, dpr: window.devicePixelRatio, " +
        "url: location.href, title: document.title })";
}
