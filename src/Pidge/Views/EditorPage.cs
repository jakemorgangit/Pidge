namespace Pidge.Views;

/// <summary>The rich-text editor hosted in the compose window's WebView2.</summary>
internal static class EditorPage
{
    public const string Html = """
<!DOCTYPE html>
<html><head><meta charset="utf-8">
<style>
  :root { --bg:#13161E; --bar:#191D27; --btn:#212634; --hover:#2A3042; --text:#E8EAF0; --muted:#A0A8BA; --accent:#7C6CFF; }
  html,body { margin:0; height:100%; background:var(--bg); font-family:'Segoe UI Variable Text','Segoe UI',sans-serif; }
  body { display:flex; flex-direction:column; }
  #bar { display:flex; flex-wrap:wrap; gap:2px; align-items:center; padding:6px 8px; background:var(--bar); border-bottom:1px solid #262B3A; user-select:none; }
  #bar button, #bar select, #bar label { height:30px; min-width:30px; border:0; border-radius:6px; background:transparent; color:var(--muted);
      font:13px 'Segoe UI'; cursor:pointer; display:inline-flex; align-items:center; justify-content:center; padding:0 7px; }
  #bar button:hover, #bar label:hover, #bar select:hover { background:var(--hover); color:var(--text); }
  #bar button.on { background:#262150; color:var(--text); }
  #bar .sep { width:1px; height:18px; background:#2A3042; margin:0 5px; }
  #bar select { background:var(--btn); color:var(--text); padding:0 6px; }
  #bar input[type=color] { width:0; height:0; opacity:0; position:absolute; }
  .ico { font-family:'Segoe Fluent Icons','Segoe MDL2 Assets'; font-size:14px; }
  #wrap { flex:1; overflow:auto; padding:14px; }
  #ed { min-height:calc(100% - 4px); box-sizing:border-box; background:#fff; color:#1d2027; border-radius:10px; padding:20px 24px; outline:none;
        font-family:'Segoe UI',Arial,sans-serif; font-size:14px; line-height:1.5; word-wrap:break-word; }
  #ed blockquote { border-left:3px solid #d0d4dc; margin:0 0 0 2px; padding-left:12px; color:#555; }
  #ed img { max-width:100%; }
  #ed a { color:#5b4fd6; }
  ::-webkit-scrollbar { width:10px; } ::-webkit-scrollbar-thumb { background:#2A3042; border-radius:5px; }
</style></head>
<body>
<div id="bar">
  <button data-cmd="bold" title="Bold (Ctrl+B)"><b>B</b></button>
  <button data-cmd="italic" title="Italic (Ctrl+I)"><i>I</i></button>
  <button data-cmd="underline" title="Underline (Ctrl+U)"><u>U</u></button>
  <button data-cmd="strikeThrough" title="Strikethrough"><s>S</s></button>
  <span class="sep"></span>
  <select id="font" title="Font">
    <option value="Segoe UI, Arial, sans-serif">Sans</option>
    <option value="Georgia, 'Times New Roman', serif">Serif</option>
    <option value="Consolas, 'Courier New', monospace">Mono</option>
  </select>
  <select id="size" title="Size">
    <option value="2">Small</option><option value="3" selected>Normal</option><option value="4">Large</option><option value="6">Huge</option>
  </select>
  <label title="Text colour"><span class="ico">&#xE8D3;</span><input type="color" id="fore" value="#d0342c"></label>
  <label title="Highlight"><span class="ico">&#xE7E6;</span><input type="color" id="back" value="#fff3a3"></label>
  <span class="sep"></span>
  <button data-cmd="insertUnorderedList" title="Bulleted list"><span class="ico">&#xE8FD;</span></button>
  <button data-cmd="insertOrderedList" title="Numbered list"><b style="font-size:12px">1.</b></button>
  <button data-cmd="outdent" title="Decrease indent"><span class="ico">&#xE72B;</span></button>
  <button data-cmd="indent" title="Increase indent"><span class="ico">&#xE72A;</span></button>
  <span class="sep"></span>
  <button data-cmd="justifyLeft" title="Align left"><span class="ico">&#xE8E4;</span></button>
  <button data-cmd="justifyCenter" title="Centre"><span class="ico">&#xE8E3;</span></button>
  <button data-cmd="justifyRight" title="Align right"><span class="ico">&#xE8E2;</span></button>
  <span class="sep"></span>
  <button id="link" title="Insert link (Ctrl+K)"><span class="ico">&#xE71B;</span></button>
  <button data-cmd="formatBlock" data-arg="blockquote" title="Quote"><span class="ico">&#xE9B1;</span></button>
  <button data-cmd="insertHorizontalRule" title="Divider">&#8213;</button>
  <button data-cmd="removeFormat" title="Clear formatting"><span class="ico">&#xE894;</span></button>
</div>
<div id="wrap"><div id="ed" contenteditable="true" spellcheck="true"></div></div>
<script>
  const ed = document.getElementById('ed');
  const post = m => window.chrome && window.chrome.webview && window.chrome.webview.postMessage(m);
  document.execCommand('styleWithCSS', false, true);
  document.execCommand('defaultParagraphSeparator', false, 'p');

  document.querySelectorAll('#bar button[data-cmd]').forEach(b => {
    b.addEventListener('mousedown', e => e.preventDefault());
    b.addEventListener('click', () => { document.execCommand(b.dataset.cmd, false, b.dataset.arg || null); ed.focus(); refresh(); dirty(); });
  });
  const keep = el => el.addEventListener('mousedown', e => e.stopPropagation());
  let saved = null;
  const save = () => { const s = getSelection(); if (s.rangeCount && ed.contains(s.anchorNode)) saved = s.getRangeAt(0).cloneRange(); };
  const restore = () => { if (saved) { const s = getSelection(); s.removeAllRanges(); s.addRange(saved); } };
  ['font','size','fore','back'].forEach(id => { const el = document.getElementById(id); el.addEventListener('focus', save); el.addEventListener('mousedown', save); });
  document.getElementById('font').addEventListener('change', e => { restore(); document.execCommand('fontName', false, e.target.value); ed.focus(); dirty(); });
  document.getElementById('size').addEventListener('change', e => { restore(); document.execCommand('fontSize', false, e.target.value); ed.focus(); dirty(); });
  document.getElementById('fore').addEventListener('input', e => { restore(); document.execCommand('foreColor', false, e.target.value); dirty(); });
  document.getElementById('back').addEventListener('input', e => { restore(); document.execCommand('hiliteColor', false, e.target.value); dirty(); });
  const link = () => {
    save();
    const url = prompt('Link address', 'https://');
    if (!url || url === 'https://') return;
    restore();
    if (getSelection().isCollapsed) document.execCommand('insertHTML', false, '<a href="' + url.replace(/"/g,'&quot;') + '">' + url.replace(/</g,'&lt;') + '</a>');
    else document.execCommand('createLink', false, url);
    dirty();
  };
  document.getElementById('link').addEventListener('mousedown', e => e.preventDefault());
  document.getElementById('link').addEventListener('click', link);

  function refresh() {
    document.querySelectorAll('#bar button[data-cmd]').forEach(b => {
      try { b.classList.toggle('on', ['bold','italic','underline','strikeThrough','insertUnorderedList','insertOrderedList'].includes(b.dataset.cmd) && document.queryCommandState(b.dataset.cmd)); } catch {}
    });
  }
  let isDirty = false;
  function dirty() { if (!isDirty) { isDirty = true; post('dirty'); } }
  ed.addEventListener('input', dirty);
  document.addEventListener('selectionchange', refresh);
  document.addEventListener('keydown', e => {
    if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); post('send'); }
    else if (e.ctrlKey && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); link(); }
    else if (e.ctrlKey && e.shiftKey && (e.key === 's' || e.key === 'S')) { e.preventDefault(); post('save'); }
  });
  document.addEventListener('click', e => { if (e.target.closest('a') && !e.ctrlKey) e.preventDefault(); });

  window.setContent = html => { ed.innerHTML = html; isDirty = false; };
  window.getContent = () => ed.innerHTML;
  window.setSignature = html => { const s = ed.querySelector('.qm-sig'); if (s) s.innerHTML = html; };
  window.focusEditor = () => {
    ed.focus();
    const r = document.createRange(); r.setStart(ed, 0); r.collapse(true);
    const s = getSelection(); s.removeAllRanges(); s.addRange(r);
  };
</script>
</body></html>
""";
}
