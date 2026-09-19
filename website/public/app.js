const repo = "yannickfan67-ai/UN_Nexo";
const api = "https://api.github.com/repos/" + repo + "/releases?per_page=10";

const releaseTag = document.getElementById("release-tag");
const releaseDate = document.getElementById("release-date");
const releaseSummary = document.getElementById("release-summary");
const downloadList = document.getElementById("download-list");

document.getElementById("year").textContent = "© " + new Date().getFullYear();

function platformFor(name) {
  const n = name.toLowerCase();
  if (n.endsWith(".exe")) return { title: "Windows x64", detail: "Self-contained EXE", rank: 1 };
  if (n.endsWith(".deb")) return { title: "Debian / Ubuntu / Mint x64", detail: "DEB package", rank: 2 };
  if (n.endsWith(".rpm")) return { title: "Fedora / RPM Linux x64", detail: "RPM package", rank: 3 };
  return null;
}

function formatBytes(bytes) {
  if (!bytes) return "";
  const mb = bytes / 1024 / 1024;
  return mb.toFixed(mb >= 10 ? 0 : 1) + " MB";
}

function makeDownload(asset) {
  const p = platformFor(asset.name);
  if (!p) return null;
  const a = document.createElement("a");
  a.className = "download-item";
  a.href = asset.browser_download_url;
  a.innerHTML = "<div><strong>" + p.title + "</strong><span>" + p.detail + " · " + formatBytes(asset.size) + "</span></div><b>↓</b>";
  return { node: a, rank: p.rank };
}

async function loadRelease() {
  try {
    const response = await fetch(api, { headers: { "Accept": "application/vnd.github+json" } });
    if (!response.ok) throw new Error("GitHub API " + response.status);
    const releases = await response.json();
    const release = releases.find(function (item) { return !item.draft; });
    if (!release) throw new Error("No published release");

    releaseTag.textContent = release.tag_name + (release.prerelease ? " · prerelease" : "");
    releaseDate.textContent = new Date(release.published_at || release.created_at).toLocaleDateString(undefined, {
      year: "numeric", month: "short", day: "numeric"
    });
    releaseSummary.textContent = release.name || ("UN_Nexo " + release.tag_name);

    const items = release.assets.map(makeDownload).filter(Boolean).sort(function (a, b) { return a.rank - b.rank; });
    const all = document.createElement("a");
    all.className = "download-item";
    all.href = release.html_url;
    all.target = "_blank";
    all.rel = "noreferrer";
    all.innerHTML = "<div><strong>Release notes & checksums</strong><span>View this release on GitHub</span></div><b>↗</b>";

    downloadList.replaceChildren.apply(downloadList, items.map(function (item) { return item.node; }).concat([all]));
  } catch (error) {
    releaseTag.textContent = "GitHub Releases";
    releaseDate.textContent = "";
    releaseSummary.textContent = "The live release feed is unavailable right now. Open GitHub Releases to download UN_Nexo.";
  }
}

loadRelease();
