using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Limen;

/// Builds the single-file xterm.js page hosted in WebView2. The library assets
/// are embedded so the terminal works without network access.
public static class TerminalHtml
{
    private static string? _cached;

    public static string Build() => _cached ??= Template
        .Replace("/*XTERM_CSS*/", ReadResource("Assets.xterm.css"))
        .Replace("/*XTERM_JS*/", ReadResource("Assets.xterm.js"))
        .Replace("/*FIT_JS*/", ReadResource("Assets.addon-fit.js"));

    /// xterm palette matching the app theme. Sent to the page on load and again
    /// whenever the user flips the theme, so the terminal never sits as a black
    /// rectangle inside a light window.
    public static string PaletteJson(bool dark) => JsonSerializer.Serialize(dark
        ? new Dictionary<string, string>
        {
            ["background"] = "#14161A", ["foreground"] = "#D6DBE3",
            ["cursor"] = "#4C8DF6", ["cursorAccent"] = "#14161A",
            ["selectionBackground"] = "#243D5A", ["selectionInactiveBackground"] = "#232830",
            ["black"] = "#14161A", ["red"] = "#F0685F", ["green"] = "#3FBF6E", ["yellow"] = "#E9C46A",
            ["blue"] = "#5AA0F8", ["magenta"] = "#C792EA", ["cyan"] = "#4FC3D9", ["white"] = "#D6DBE3",
            ["brightBlack"] = "#5D6570", ["brightRed"] = "#FF8B82", ["brightGreen"] = "#5FD98C",
            ["brightYellow"] = "#F5D98B", ["brightBlue"] = "#7FB6FA", ["brightMagenta"] = "#DDB0F5",
            ["brightCyan"] = "#77D8EA", ["brightWhite"] = "#F2F5F9"
        }
        : new Dictionary<string, string>
        {
            ["background"] = "#FBFBFC", ["foreground"] = "#24282E",
            ["cursor"] = "#1F6FEB", ["cursorAccent"] = "#FBFBFC",
            ["selectionBackground"] = "#D3E3F9", ["selectionInactiveBackground"] = "#E7EAEE",
            ["black"] = "#24282E", ["red"] = "#C7362B", ["green"] = "#1A7F37", ["yellow"] = "#9A6700",
            ["blue"] = "#1F6FEB", ["magenta"] = "#8250DF", ["cyan"] = "#1B7C8C", ["white"] = "#6E7781",
            ["brightBlack"] = "#6E7781", ["brightRed"] = "#E5534B", ["brightGreen"] = "#2DA44E",
            ["brightYellow"] = "#BF8700", ["brightBlue"] = "#4C8DF6", ["brightMagenta"] = "#A475F9",
            ["brightCyan"] = "#2FA9C4", ["brightWhite"] = "#24282E"
        });

    /// Colours for the page's own chrome — the find bar. Kept apart from the
    /// xterm palette so no stray key reaches term.options.theme.
    public static string UiJson(bool dark) => JsonSerializer.Serialize(dark
        ? new Dictionary<string, string>
        {
            ["bg"] = "#22262C", ["field"] = "#14171C", ["line"] = "#3D444E",
            ["ink"] = "#E7EBF1", ["muted"] = "#A6AEBB", ["accent"] = "#4C8DF6", ["hover"] = "#31373F"
        }
        : new Dictionary<string, string>
        {
            ["bg"] = "#FFFFFF", ["field"] = "#FFFFFF", ["line"] = "#CBD0D8",
            ["ink"] = "#16191D", ["muted"] = "#5B6472", ["accent"] = "#1F6FEB", ["hover"] = "#F2F4F7"
        });

    private static string ReadResource(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private const string Template = """
        <!doctype html><html><head><meta charset="utf-8"><style>
        html,body{width:100%;height:100%;margin:0;background:#14161a;overflow:hidden}
        body{position:relative}
        #terminal{width:100%;height:100%;box-sizing:border-box;padding:10px 12px}
        #find{position:absolute;top:10px;right:16px;z-index:10;display:flex;align-items:center;gap:6px;
          padding:6px;border-radius:9px;background:var(--ui-bg);border:1px solid var(--ui-line);
          color:var(--ui-ink);box-shadow:0 8px 22px rgba(0,0,0,.30);
          font:12px/1.2 "Segoe UI Variable Text","Segoe UI","Malgun Gothic",sans-serif}
        #find[hidden]{display:none}
        #findq{width:190px;padding:5px 8px;border-radius:6px;border:1px solid var(--ui-line);
          background:var(--ui-field);color:var(--ui-ink);outline:none;font:inherit}
        #findq:focus{border-color:var(--ui-accent)}
        #findn{min-width:52px;text-align:center;color:var(--ui-muted);font-variant-numeric:tabular-nums}
        #find button{width:24px;height:24px;padding:0;border:0;border-radius:5px;cursor:pointer;
          background:transparent;color:var(--ui-muted);font:13px/1 inherit}
        #find button:hover{background:var(--ui-hover);color:var(--ui-ink)}
        /*XTERM_CSS*/
        .xterm-viewport::-webkit-scrollbar{width:11px}
        .xterm-viewport::-webkit-scrollbar-track{background:transparent}
        .xterm-viewport::-webkit-scrollbar-thumb{background:rgba(140,150,165,.34);border-radius:6px;
          border:3px solid transparent;background-clip:content-box}
        .xterm-viewport::-webkit-scrollbar-thumb:hover{background:rgba(160,172,188,.6);
          border:3px solid transparent;background-clip:content-box}
        </style></head><body><div id="terminal"></div>
        <div id="find" hidden>
          <input id="findq" type="text" placeholder="찾기" spellcheck="false">
          <span id="findn">0/0</span>
          <button id="findprev" title="이전 (Shift+Enter)">&#8593;</button>
          <button id="findnext" title="다음 (Enter)">&#8595;</button>
          <button id="findx" title="닫기 (Esc)">&#10005;</button>
        </div>
        <script>/*XTERM_JS*/</script><script>/*FIT_JS*/</script><script>
        const term = new Terminal({
          cursorBlink:true, cursorStyle:'bar', cursorWidth:2, scrollback:20000, convertEol:false,
          fontFamily:'"Cascadia Mono", Consolas, D2Coding, monospace', fontSize:14, lineHeight:1.2,
          letterSpacing:0, drawBoldTextInBrightColors:true, minimumContrastRatio:1.2,
          theme:{background:'#14161a',foreground:'#d6dbe3',cursor:'#4c8df6'}
        });
        const fitter = new FitAddon.FitAddon();
        term.loadAddon(fitter);
        term.open(document.getElementById('terminal'));

        const send = (type, extra = {}) => chrome.webview.postMessage(Object.assign({type}, extra));
        const toBase64 = bytes => { let s = ''; for (const b of bytes) s += String.fromCharCode(b); return btoa(s); };

        const MIN_FONT = 9, MAX_FONT = 28, BASE_FONT = 14;
        const setFont = (size, notify = true) => {
          const next = Math.min(MAX_FONT, Math.max(MIN_FONT, Math.round(size)));
          if (next === term.options.fontSize) return;
          term.options.fontSize = next;
          refit();
          if (notify) send('fontsize', {size: next});
        };

        const applyTheme = (theme, ui) => {
          term.options.theme = theme;
          document.body.style.background = theme.background;
          document.getElementById('terminal').style.background = theme.background;
          if (!ui) return;
          const root = document.documentElement.style;
          for (const [key, value] of Object.entries(ui)) root.setProperty('--ui-' + key, value);
        };

        term.onData(data => send('input', {data: toBase64(new TextEncoder().encode(data))}));
        term.onResize(size => send('resize', {cols: size.cols, rows: size.rows}));
        term.parser.registerOscHandler(7, data => {
          try {
            const match = /^file:\/\/[^/]*(\/.*)$/.exec(data);
            if (match) {
              let path = match[1];
              try { path = decodeURIComponent(path); } catch {}
              send('cwd', {path});
            }
          } catch {}
          return false;
        });

        // ---- 스크롤백 검색 ----------------------------------------------
        // The bundled xterm has no search addon, so matches are found by
        // walking the buffer. A match that straddles a wrapped line boundary
        // is not found — the tradeoff for keeping this dependency-free.
        const findBar = document.getElementById('find');
        const findInput = document.getElementById('findq');
        const findCount = document.getElementById('findn');
        let matches = [];
        let matchIndex = -1;
        let lastQuery = '';

        const collect = query => {
          const found = [];
          const needle = query.toLowerCase();
          if (!needle) return found;
          const buffer = term.buffer.active;
          for (let y = 0; y < buffer.length; y++) {
            const line = buffer.getLine(y);
            if (!line) continue;
            const text = line.translateToString(true).toLowerCase();
            let at = text.indexOf(needle);
            while (at !== -1) {
              found.push({y, x: at});
              at = text.indexOf(needle, at + needle.length);
            }
          }
          return found;
        };

        const showCount = () => {
          findCount.textContent = matches.length === 0
            ? (lastQuery ? '없음' : '0/0')
            : (matchIndex + 1) + '/' + matches.length;
        };

        const reveal = () => {
          if (matchIndex < 0 || matchIndex >= matches.length) return;
          const hit = matches[matchIndex];
          term.select(hit.x, hit.y, lastQuery.length);
          term.scrollToLine(Math.max(0, hit.y - Math.floor(term.rows / 2)));
          showCount();
        };

        const research = () => {
          lastQuery = findInput.value;
          matches = collect(lastQuery);
          matchIndex = matches.length ? 0 : -1;
          if (matchIndex < 0) {
            term.clearSelection();
            showCount();
            return;
          }
          reveal();
        };

        const step = delta => {
          if (lastQuery !== findInput.value) { research(); return; }
          if (!matches.length) return;
          matchIndex = (matchIndex + delta + matches.length) % matches.length;
          reveal();
        };

        const openFind = () => {
          findBar.hidden = false;
          findInput.focus();
          findInput.select();
          if (findInput.value) research();
        };

        const closeFind = () => {
          findBar.hidden = true;
          term.clearSelection();
          term.focus();
        };

        findInput.addEventListener('input', research);
        findInput.addEventListener('keydown', ev => {
          if (ev.key === 'Enter') { ev.preventDefault(); step(ev.shiftKey ? -1 : 1); }
          else if (ev.key === 'Escape') { ev.preventDefault(); closeFind(); }
        });
        document.getElementById('findnext').addEventListener('click', () => step(1));
        document.getElementById('findprev').addEventListener('click', () => step(-1));
        document.getElementById('findx').addEventListener('click', closeFind);

        document.getElementById('terminal').addEventListener('wheel', ev => {
          if (!ev.ctrlKey) return;
          ev.preventDefault();
          setFont(term.options.fontSize + (ev.deltaY < 0 ? 1 : -1));
        }, {passive: false});

        term.attachCustomKeyEventHandler(ev => {
          if (ev.type !== 'keydown') return true;
          if (ev.key === 'F3') { step(ev.shiftKey ? -1 : 1); return false; }
          if (ev.key === 'Escape' && !findBar.hidden) { closeFind(); return false; }
          if (!ev.ctrlKey || ev.altKey) return true;
          if (ev.key === 'f' || ev.key === 'F') { openFind(); return false; }
          if (ev.key === '=' || ev.key === '+') { setFont(term.options.fontSize + 1); return false; }
          if (ev.key === '-' || ev.key === '_') { setFont(term.options.fontSize - 1); return false; }
          if (ev.key === '0') { setFont(BASE_FONT); return false; }
          return true;
        });

        document.getElementById('terminal').addEventListener('contextmenu', ev => {
          ev.preventDefault();
          if (term.hasSelection()) {
            send('copy', {text: term.getSelection()});
            term.clearSelection();
          } else {
            send('paste');
          }
          term.focus();
        }, true);

        chrome.webview.addEventListener('message', ev => {
          const m = ev.data;
          if (m.type === 'output') {
            term.write(Uint8Array.from(atob(m.data), c => c.charCodeAt(0)), () => {
              if (!findBar.hidden && lastQuery) { matches = collect(lastQuery); showCount(); }
            });
          }
          else if (m.type === 'notice') term.write(m.text);
          else if (m.type === 'theme') applyTheme(m.theme, m.ui);
          else if (m.type === 'font') setFont(m.size, false);
        });

        window.terminalFocus = () => term.focus();
        const refit = () => { try { fitter.fit(); } catch {} };
        new ResizeObserver(refit).observe(document.body);
        requestAnimationFrame(() => { refit(); send('ready', {cols: term.cols, rows: term.rows}); term.focus(); });
        </script></body></html>
        """;
}
