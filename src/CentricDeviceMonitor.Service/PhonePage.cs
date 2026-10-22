namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// The read-only phone page of the monitored PCs. Self-contained (no outside scripts or fonts): it reads
/// /wu/phone/data with the same link key every 15 seconds and draws one card per PC, in light or dark to match the phone.
/// </summary>
internal static class PhonePage
{
    public const string InvalidLinkHtml = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Link not valid</title><style>body{font-family:system-ui,"Segoe UI",sans-serif;margin:32px 20px;color:#1d1d1f}
        @media (prefers-color-scheme:dark){body{background:#111;color:#eee}}</style></head>
        <body><h2>This link is not valid</h2><p>Open Network PCs on the main PC and use the phone link shown there. A new link replaces the old one.</p></body></html>
        """;

    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
        <meta name="robots" content="noindex">
        <title>Network PCs</title>
        <style>
          :root { --bg:#f3f4f6; --card:#ffffff; --text:#1d1d1f; --muted:#6b7280; --line:#e5e7eb; --bar:#e5e7eb;
                  --ok:#16a34a; --warn:#d97706; --bad:#dc2626; --idle:#6b7280; --accent:#2563eb; }
          @media (prefers-color-scheme: dark) {
            :root { --bg:#0f1115; --card:#1a1d23; --text:#f3f4f6; --muted:#9ca3af; --line:#2b2f37; --bar:#2b2f37;
                    --ok:#22c55e; --warn:#f59e0b; --bad:#f87171; --idle:#9ca3af; --accent:#60a5fa; }
          }
          * { box-sizing: border-box; }
          body { margin:0; background:var(--bg); color:var(--text); font:15px/1.4 system-ui,"Segoe UI",Roboto,sans-serif; }
          header { padding:18px 16px 8px; }
          h1 { font-size:22px; margin:0; }
          .sub { color:var(--muted); font-size:13px; margin-top:2px; }
          .stale { margin:8px 16px 0; padding:10px 12px; border-radius:10px; background:color-mix(in srgb, var(--warn) 15%, transparent); color:var(--text); font-size:13px; display:none; }
          .chips { display:flex; gap:8px; flex-wrap:wrap; padding:10px 16px 4px; }
          .chip { background:var(--card); border:1px solid var(--line); border-radius:999px; padding:5px 12px; font-size:13px; }
          .chip b { font-size:15px; margin-right:4px; }
          .search { margin:8px 16px 4px; }
          .search input { width:100%; padding:10px 12px; border-radius:10px; border:1px solid var(--line); background:var(--card); color:var(--text); font-size:15px; }
          main { display:grid; grid-template-columns:repeat(auto-fill,minmax(290px,1fr)); gap:12px; padding:10px 16px 28px; }
          .card { background:var(--card); border:1px solid var(--line); border-radius:14px; padding:14px; }
          .top { display:flex; justify-content:space-between; align-items:flex-start; gap:8px; }
          .name { font-weight:600; font-size:16px; word-break:break-word; }
          .group { color:var(--muted); font-size:12px; }
          .pill { font-size:12px; font-weight:600; padding:3px 10px; border-radius:999px; color:#fff; white-space:nowrap; }
          .Online { background:var(--ok); } .Warning { background:var(--warn); } .Offline { background:var(--bad); } .Checking { background:var(--idle); }
          .row { display:grid; grid-template-columns:52px 1fr 48px; align-items:center; gap:8px; margin-top:8px; font-size:13px; }
          .row .label { color:var(--muted); } .row .value { text-align:right; font-weight:600; }
          .bar { height:7px; border-radius:4px; background:var(--bar); overflow:hidden; }
          .bar i { display:block; height:100%; background:var(--accent); border-radius:4px; }
          .bar i.high { background:var(--warn); } .bar i.full { background:var(--bad); }
          .detail { color:var(--muted); font-size:12px; margin-top:8px; }
          .note { color:var(--warn); font-size:12px; margin-top:4px; }
          .msg { color:var(--muted); font-size:13px; margin-top:8px; }
          footer { color:var(--muted); font-size:12px; text-align:center; padding:0 16px 24px; }
        </style>
        </head>
        <body>
        <header><h1>Network PCs</h1><div class="sub" id="sub">Loading...</div></header>
        <div class="stale" id="stale"></div>
        <div class="chips" id="chips"></div>
        <div class="search"><input id="q" type="search" placeholder="Search PCs" autocomplete="off"></div>
        <main id="list"></main>
        <footer>Read-only view from Windows Utility. Refreshes every 15 seconds.</footer>
        <script>
        (function () {
          var key = new URLSearchParams(location.search).get("k") || "";
          var data = null;
          function esc(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" }[c]; }); }
          function bar(percent) {
            var p = Math.max(0, Math.min(100, percent || 0));
            var cls = p >= 95 ? "full" : p >= 85 ? "high" : "";
            return '<div class="bar"><i class="' + cls + '" style="width:' + p.toFixed(0) + '%"></i></div>';
          }
          function row(label, percent, text) {
            return '<div class="row"><span class="label">' + esc(label) + '</span>' + bar(percent) + '<span class="value">' + esc(text) + '</span></div>';
          }
          function card(pc) {
            var html = '<div class="card"><div class="top"><div><div class="name">' + esc(pc.name) + '</div>'
              + (pc.group ? '<div class="group">' + esc(pc.group) + '</div>' : '') + '</div>'
              + '<span class="pill ' + esc(pc.statusKind) + '">' + esc(pc.statusLabel) + '</span></div>';
            if (pc.cpuPercent != null) {
              html += row("CPU", pc.cpuPercent, pc.cpuPercent.toFixed(0) + "%");
              if (pc.ramPercent != null) html += row("RAM", pc.ramPercent, pc.ramPercent.toFixed(0) + "%");
              (pc.disks || []).forEach(function (d) { html += row(d.name, d.usedPercent, d.usedPercent.toFixed(0) + "%"); });
              var details = [];
              if (pc.temperature) details.push("Temp " + pc.temperature);
              (pc.disks || []).forEach(function (d) { details.push(d.name + " " + d.free + " free"); });
              if (pc.signedIn) details.push(pc.signedIn);
              html += '<div class="detail">' + esc(details.join("  •  ")) + '</div>';
            } else if (pc.message) {
              html += '<div class="msg">' + esc(pc.message) + '</div>';
            }
            (pc.notes || []).forEach(function (n) { html += '<div class="note">' + esc(n) + '</div>'; });
            if (pc.lastSeen) html += '<div class="detail">' + esc(pc.lastSeen) + '</div>';
            return html + '</div>';
          }
          function render() {
            if (!data) return;
            var q = document.getElementById("q").value.trim().toLowerCase();
            var pcs = data.pcs || [];
            var shown = pcs.filter(function (pc) { return !q || (pc.name + " " + pc.group).toLowerCase().indexOf(q) >= 0; });
            var rank = { Offline: 0, Warning: 1, Checking: 2, Online: 3 };
            shown.sort(function (a, b) { return (rank[a.statusKind] - rank[b.statusKind]) || a.name.localeCompare(b.name); });
            document.getElementById("list").innerHTML = shown.length ? shown.map(card).join("") : '<div class="msg">' + (pcs.length ? "No PC matches." : "No PCs are monitored yet.") + '</div>';
            var online = pcs.filter(function (pc) { return pc.statusKind === "Online"; }).length;
            var warn = pcs.filter(function (pc) { return pc.statusKind === "Warning"; }).length;
            var off = pcs.filter(function (pc) { return pc.statusKind === "Offline"; }).length;
            document.getElementById("chips").innerHTML = '<span class="chip"><b>' + online + '</b>online</span>'
              + '<span class="chip"><b>' + warn + '</b>need attention</span><span class="chip"><b>' + off + '</b>offline</span>';
            var updated = data.updatedUtc ? new Date(data.updatedUtc) : null;
            document.getElementById("sub").textContent = "From " + (data.mainPcName || "the main PC") + (updated && updated.getFullYear() > 2000 ? "  •  checked " + updated.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : "");
            var stale = document.getElementById("stale");
            var old = !updated || updated.getFullYear() < 2000 || Date.now() - updated.getTime() > 3 * 60 * 1000;
            stale.style.display = old ? "block" : "none";
            stale.textContent = old ? "The Windows Utility dashboard on " + (data.mainPcName || "the main PC") + " has not checked the PCs recently. It must be running (it can stay in the notification area)." : "";
          }
          function load() {
            fetch("/wu/phone/data?k=" + encodeURIComponent(key), { cache: "no-store" })
              .then(function (r) { if (!r.ok) throw new Error(r.status === 401 ? "This link is no longer valid." : "The main PC answered with " + r.status + "."); return r.json(); })
              .then(function (d) { data = d; render(); })
              .catch(function (e) { document.getElementById("sub").textContent = e.message || "The main PC cannot be reached. Is this phone on the same Wi-Fi?"; });
          }
          document.getElementById("q").addEventListener("input", render);
          load();
          setInterval(load, 15000);
        })();
        </script>
        </body>
        </html>
        """;
}
