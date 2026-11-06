const header = document.querySelector("[data-header]");
const navToggle = document.querySelector("[data-nav-toggle]");
const nav = document.querySelector("[data-nav]");

const updateHeader = () => {
  header?.classList.toggle("is-scrolled", window.scrollY > 8);
};

updateHeader();
window.addEventListener("scroll", updateHeader, { passive: true });

navToggle?.addEventListener("click", () => {
  const open = nav?.classList.toggle("is-open") ?? false;
  navToggle.setAttribute("aria-expanded", String(open));
});

nav?.querySelectorAll("a").forEach((link) => {
  link.addEventListener("click", () => {
    nav.classList.remove("is-open");
    navToggle?.setAttribute("aria-expanded", "false");
  });
});

document.querySelectorAll("[data-delay]").forEach((element) => {
  element.style.setProperty("--delay", `${element.dataset.delay}ms`);
});

const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
const revealElements = document.querySelectorAll(".reveal");

if (prefersReducedMotion || !("IntersectionObserver" in window)) {
  revealElements.forEach((element) => element.classList.add("is-visible"));
} else {
  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      });
    },
    { threshold: 0.1, rootMargin: "0px 0px -6% 0px" }
  );
  revealElements.forEach((element) => observer.observe(element));
}

document.querySelectorAll("[data-year]").forEach((element) => {
  element.textContent = new Date().getFullYear().toString();
});

const iconPaths = {
  home: "M3.5 10.5 L12 3.5 L20.5 10.5 M5.5 9.5 V20.5 H18.5 V9.5 M9.5 20.5 V14.5 H14.5 V20.5",
  info: "M12 21 A9 9 0 1 1 12 3 A9 9 0 1 1 12 21 M12 11 V16.5 M12 7.5 V8.2",
  "context-menu": "M4 6.5 H12 M4 12 H12 M4 17.5 H12 M16.5 6.5 H20 M16.5 12 H20 M16.5 17.5 H20",
  preset: "M12 2.5 C15.5 6 16 10.5 12 14.5 C8 10.5 8.5 6 12 2.5 Z M12 14.5 V21 M8.5 17.5 C7 18.5 6.2 19.7 6 21 M15.5 17.5 C17 18.5 17.8 19.7 18 21",
  update: "M20 12 A8 8 0 1 1 12 4 M20 12 V6.5 M20 12 H14.5",
  shield: "M12 3 L20 6.2 V11 C20 16 16.5 19.6 12 21 C7.5 19.6 4 16 4 11 V6.2 Z",
  activity: "M3 12.5 H7 L10 5.5 L14 19 L17 12.5 H21",
  apps: "M4 4 H9.5 V9.5 H4 Z M14.5 4 H20 V9.5 H14.5 Z M4 14.5 H9.5 V20 H4 Z M14.5 14.5 H20 V20 H14.5 Z",
  download: "M12 3.5 V15 M7 10.5 L12 15.5 L17 10.5 M4.5 20.5 H19.5",
  uninstall: "M4 8 L12 3 L20 8 V17 L12 22 L4 17 Z M4 8 L12 13 L20 8 M12 13 V22",
  startup: "M12 2.5 C15.5 6 16 10.5 12 14.5 C8 10.5 8.5 6 12 2.5 Z M12 14.5 V21 M8.5 17.5 C7 18.5 6.2 19.7 6 21 M15.5 17.5 C17 18.5 17.8 19.7 18 21",
  cleanup: "M12 3 L13.8 10.2 L21 12 L13.8 13.8 L12 21 L10.2 13.8 L3 12 L10.2 10.2 Z",
  network: "M3 12 H21 M12 21 C16 17 16 7 12 3 C8 7 8 17 12 21 M4.2 7.5 H19.8 M4.2 16.5 H19.8",
  gear: "M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z M12 15.5 A3.5 3.5 0 1 1 12 8.5 A3.5 3.5 0 1 1 12 15.5",
  power: "M12 3.5 V11.5 M6.8 6.6 A8 8 0 1 0 17.2 6.6",
  chip: "M7 7 H17 V17 H7 Z M9.5 4 V7 M14.5 4 V7 M9.5 17 V20 M14.5 17 V20 M4 9.5 H7 M4 14.5 H7 M17 9.5 H20 M17 14.5 H20",
  game: "M7.5 8.5 H16.5 C19.5 8.5 20.5 10.5 20 15 C19.7 17.3 17.5 18 16 16 L14.5 14.5 H9.5 L8 16 C6.5 18 4.3 17.3 4 15 C3.5 10.5 4.5 8.5 7.5 8.5 Z M8 11 V13.5 M6.8 12.2 H9.2 M15.5 11.3 V11.7 M17.4 13.2 V13.6",
  sync: "M4 8 H17 M14 5 L17 8 L14 11 M20 16 H7 M10 13 L7 16 L10 19",
  terminal: "M3 5 H21 V19 H3 Z M7 9.5 L10 12 L7 14.5 M12.5 14.5 H17",
  privacy: "M2.5 12 C5.5 6.8 18.5 6.8 21.5 12 C18.5 17.2 5.5 17.2 2.5 12 Z M12 15 A3 3 0 1 1 12 9 A3 3 0 1 1 12 15 M4 3.5 L20 20.5",
  code: "M8 8 L3 12 L8 16 M16 8 L21 12 L16 16 M14 4.5 L10 19.5",
  sliders: "M4 7 H20 M4 12 H20 M4 17 H20 M9 4.8 V9.2 M16 9.8 V14.2 M7 14.8 V19.2",
  settings: "M4 7 H20 M4 12 H20 M4 17 H20 M9 4.8 V9.2 M16 9.8 V14.2 M7 14.8 V19.2",
  sun: "M12 16.5 A4.5 4.5 0 1 1 12 7.5 A4.5 4.5 0 1 1 12 16.5 M12 2.5 V5 M12 19 V21.5 M2.5 12 H5 M19 12 H21.5 M5.3 5.3 L7 7 M17 17 L18.7 18.7 M18.7 5.3 L17 7 M7 17 L5.3 18.7",
  moon: "M20 14.5 A8.5 8.5 0 1 1 9.5 4 A7 7 0 0 0 20 14.5 Z",
  check: "M4 12.5 L9.5 18 L20 6.5",
  folder: "M3 6.5 H9.5 L11.5 8.5 H21 V19.5 H3 Z",
  refresh: "M20 12 A8 8 0 1 1 12 4 M20 12 V6.5 M20 12 H14.5",
  list: "M4 6.5 H20 M4 12 H20 M4 17.5 H14",
  arrow: "M9.5 6 L15.5 12 L9.5 18"
};

const icon = (name, className = "") => `
  <svg class="${className}" viewBox="0 0 24 24" aria-hidden="true">
    <path d="${iconPaths[name] || iconPaths.info}"></path>
  </svg>`;

const navGroups = [
  {
    title: "总览",
    items: [
      ["home", "开荒工作台", "home", "首页与快捷操作"],
      ["system-info", "系统信息", "info", "硬件、系统版本与激活状态"]
    ]
  },
  {
    title: "常用",
    items: [
      ["software", "推荐软件", "download", "高频软件的静默安装"],
      ["presets", "一键开荒", "preset", "四套预设批量优化"],
      ["cleanup", "系统清理", "cleanup", "临时文件与缩略图缓存"]
    ]
  },
  {
    title: "优化",
    items: [
      ["execution", "执行中心", "terminal", "配置工作流、预检计划与报告"],
      ["context-menu", "右键菜单", "context-menu", "新版与经典右键菜单管理"],
      ["ui-tuning", "界面调优", "sliders", "任务栏、资源管理器与深色模式"],
      ["privacy", "隐私清理", "privacy", "遥测、广告 ID 与推荐内容"],
      ["windows-update", "Windows 更新", "update", "暂停、关闭与更新历史"],
      ["defender", "安全防护", "shield", "Defender 与防火墙"],
      ["features", "Windows 功能", "apps", "启用或禁用可选功能"],
      ["game-optimize", "游戏优化", "game", "Game DVR、游戏模式与 GPU 调度"]
    ]
  },
  {
    title: "应用",
    items: [
      ["appx", "原生应用", "apps", "Appx 包卸载与重新注册"],
      ["uninstall", "软件卸载", "uninstall", "第三方程序卸载与残留清理"],
      ["startup", "自启动", "startup", "注册表、启动文件夹与任务"]
    ]
  },
  {
    title: "工具",
    items: [
      ["performance", "资源占用", "activity", "CPU、内存、磁盘、网络与 GPU"],
      ["network", "网络工具", "network", "DNS、IPv4、Ping 与局域网互传"],
      ["service-tasks", "服务与任务", "gear", "服务启动类型与计划任务"],
      ["power", "电源管理", "power", "电源计划、合盖与快速启动"],
      ["driver", "驱动与备份", "chip", "驱动安装、删除与导出备份"],
      ["restore", "全局还原", "sync", "恢复到首次基准"],
      ["history", "操作历史", "activity", "查看并回滚历史操作"],
      ["migration", "环境克隆", "sync", "迁移软件、聊天数据与开发环境"],
      ["settings", "设置", "settings", "外观、动效、备份与关于"]
    ]
  }
].map((group) => ({
  title: group.title,
  items: group.items.map(([key, title, iconName, hint]) => ({ key, title, iconName, hint }))
}));

const pageMeta = {
  home: ["开荒工作台", "把刚装好的 Windows 11 调到顺手的状态。所有修改都会先备份，随时可以撤销。"],
  "system-info": ["系统信息", "本机硬件、系统版本与激活状态，只读，不会修改任何设置"],
  software: ["推荐软件", "装完系统先补这 7 个常用软件。winget 可用时静默安装；缺失时按需在线更新，也可导入微软官方离线包。"],
  presets: ["一键开荒", "选择一套预设，一次完成批量写入、逐项校验和失败回滚。"],
  cleanup: ["系统清理", "扫描用户临时文件、Windows 临时文件和缩略图缓存。"],
  execution: ["执行中心", "配置目标工作流，检查执行计划后统一运行；兼容旧方案 JSON 与 --unattended 命令行"],
  "context-menu": ["右键菜单", "在 Windows 11 新版菜单、经典菜单与扩展项之间选择。"],
  "ui-tuning": ["界面调优", "任务栏、资源管理器、窗口效果与系统界面统一调整。"],
  privacy: ["隐私清理", "集中管理诊断数据、广告 ID、活动历史与推荐内容。"],
  "windows-update": ["Windows 更新", "暂停、延长、恢复或彻底关闭 Windows 更新。"],
  defender: ["安全防护", "查看 Defender 状态，管理实时防护、排除项与防火墙规则。"],
  features: ["Windows 功能", "启用或禁用 Hyper-V、WSL、.NET 3.5、OpenSSH、沙盒等可选功能。"],
  "game-optimize": ["游戏优化", "管理 Game DVR、游戏模式、后台录制和 GPU 调度。"],
  appx: ["原生应用", "枚举当前用户 Appx、筛选系统应用并管理卸载记录。"],
  uninstall: ["软件卸载", "按名称、发布者或安装位置查找第三方桌面程序。"],
  startup: ["自启动", "集中查看注册表、启动文件夹、计划任务和自动服务。"],
  performance: ["资源占用", "实时 CPU / 内存 / 磁盘 / 网络 / GPU；打开页面立即采样。"],
  network: ["网络工具", "网络适配器、DNS、IPv4、连通性和局域网文件传输。"],
  "service-tasks": ["服务与任务", "搜索 Windows 服务与计划任务，并修改启动类型。"],
  power: ["电源管理", "平衡、高性能、节能和卓越性能计划，以及快速启动。"],
  driver: ["驱动与备份", "查看第三方驱动，安装 INF，删除前自动导出备份。"],
  restore: ["全局还原", "恢复到 FreshStart 首次运行时的系统基准。"],
  history: ["操作历史", "查看所有写操作、快照和回滚结果。"],
  migration: ["环境克隆", "把软件、聊天数据和开发工具链封装成可离线释放的克隆包"],
  settings: ["设置", "外观、动效、备份策略与关于信息"]
};

const appWindow = document.querySelector("[data-app-window]");
const appNav = document.querySelector("[data-app-nav]");
const appView = document.querySelector("[data-app-view]");
const appTitle = document.querySelector("[data-app-title]");
const appSearch = document.querySelector("[data-nav-search]");
const appToast = document.querySelector("[data-app-toast]");
const themeButton = document.querySelector("[data-theme-toggle]");
const sidebarButton = document.querySelector("[data-sidebar-toggle]");

let activePage = "home";
let activeTab = "builder";
let toastTimer;

const pageButton = (label, variant = "", extra = "") =>
  `<button type="button" class="app-btn ${variant}" data-demo-action ${extra}>${label}</button>`;

const pageHeader = (key, actions = "") => {
  const [title, subtitle] = pageMeta[key];
  return `
    <header class="app-page-header">
      <div><h2>${title}</h2><p>${subtitle}</p></div>
      ${actions ? `<div class="app-page-actions">${actions}</div>` : ""}
    </header>`;
};

const infoCard = (title, iconName, rows) => `
  <section class="app-card">
    <div class="app-card-icon-title">${icon(iconName)}<h3 class="app-section-title">${title}</h3></div>
    <div class="app-divider"></div>
    <dl class="app-info-list">
      ${rows.map(([label, value]) => `
        <div class="app-info-row"><dt>${label}</dt><dd>${value}</dd></div>`).join("")}
    </dl>
  </section>`;

const toggleRows = (rows) => `
  <div class="app-list">
    ${rows.map(([title, description, enabled = true]) => `
      <div class="app-toggle-row">
        <div class="app-toggle-copy"><strong>${title}</strong><p>${description}</p></div>
        <button type="button" class="app-switch ${enabled ? "is-on" : ""}" role="switch" aria-checked="${enabled}" aria-label="${title}" data-switch></button>
      </div>`).join("")}
  </div>`;

const listRows = (rows) => `
  <div class="app-list">
    ${rows.map(([title, description, state, kind = ""]) => `
      <div class="app-list-row">
        <div><strong>${title}</strong><small>${description}</small></div>
        <span class="app-status ${kind}">${state}</span>
        ${pageButton("查看", "small")}
      </div>`).join("")}
  </div>`;

const renderHome = () => `
  <div class="app-page">
    <section class="app-readiness">
      <div class="app-readiness-copy">
        <p class="app-eyebrow">SYSTEM READINESS / 01</p>
        <h2>你的 Windows，随时进入状态</h2>
        <p>把刚装好的 Windows 11 调到顺手的状态。所有修改都会先备份，随时可以撤销。</p>
        <div class="app-badges">
          <span class="app-badge"><i></i>标准权限</span>
          <span class="app-badge">◇ 本机处理 · 不上传数据</span>
        </div>
        <div class="app-triad">
          <span><strong>本地</strong><small>执行边界</small></span>
          <span><strong>可回滚</strong><small>写入策略</small></span>
          <span><strong>可审计</strong><small>操作记录</small></span>
        </div>
      </div>
      <div class="app-score"><strong>55</strong><small>就绪评分</small></div>
    </section>

    <div class="app-section-row">
      <div><p class="app-eyebrow">COMMAND DECK / 02</p><h3>快捷操作</h3><p>常用系统工具的直达入口，不修改任何配置</p></div>
      <span class="app-status">系统状态已刷新</span>
    </div>

    <div class="app-grid cols-3">
      ${[
        ["重启资源管理器", "任务栏或桌面卡死时使用，会短暂黑屏", "refresh"],
        ["电源选项", "打开电源计划与合盖行为设置", "power"],
        ["控制面板", "打开经典控制面板入口", "sliders"],
        ["设备管理器", "查看与更新硬件驱动", "chip"],
        ["磁盘管理", "分区、盘符与虚拟磁盘", "info"],
        ["服务", "管理系统服务启动类型", "gear"],
        ["注册表编辑器", "高级用户使用，修改前请先备份", "code"],
        ["环境变量", "编辑用户与系统 PATH 等变量", "terminal"],
        ["系统属性", "计算机名、性能选项与远程设置", "info"]
      ].map(([title, description, iconName]) => `
        <button type="button" class="app-quick-card" data-demo-action>
          <span class="app-quick-icon">${icon(iconName)}</span>
          <span><b>${title}</b><small>${description}</small></span>
        </button>`).join("")}
    </div>
    <p class="app-footer-note">提示：重启资源管理器会短暂黑屏；注册表编辑器与环境变量属于高级工具，修改前请先备份。</p>
  </div>`;

const renderSystemInfo = () => `
  <div class="app-page">
    ${pageHeader("system-info", `${pageButton("刷新")}${pageButton("复制全部", "primary")}`)}
    <div class="app-status-line">当前状态：系统信息采集完成 · 更新于刚刚</div>
    <div class="app-grid cols-2">
      ${infoCard("系统状态", "info", [
        ["设备名称", "FRESHSTART-DEMO"], ["Windows 版本", "Windows 11 Pro 24H2"],
        ["Build", "26100.4351"], ["激活状态", "已激活"], ["运行时间", "2 天 7 小时"]
      ])}
      ${infoCard("处理器与内存", "chip", [
        ["处理器", "AMD Ryzen 9 7950X 16-Core"], ["物理核心", "16"],
        ["逻辑处理器", "32"], ["内存总量", "64.0 GB"], ["可用内存", "43.8 GB"]
      ])}
      ${infoCard("显示与存储", "apps", [
        ["显卡", "NVIDIA GeForce RTX 4080 SUPER"], ["显存", "16.0 GB"],
        ["系统磁盘", "NVMe SSD 2 TB"], ["可用空间", "1.23 TB"]
      ])}
      ${infoCard("网络与主板", "network", [
        ["主板", "ASUS ROG STRIX X670E-E"], ["BIOS", "2506"],
        ["网卡", "2.5GbE + Wi-Fi 7"], ["IPv4", "192.168.1.42"]
      ])}
    </div>
  </div>`;

const renderSoftware = () => `
  <div class="app-page">
    ${pageHeader("software", `${pageButton("一键安装全部", "primary")}${pageButton("修复 winget")}${pageButton("刷新")}`)}
    <div class="app-status-line">winget 可用 · 已安装 5 / 8 · 2 项可更新</div>
    <div class="app-grid cols-3">
      ${[
        ["7-Zip", "解压缩工具", "已安装", "安装"],
        ["Bandizip", "压缩包预览与多线程解压", "已安装", "安装"],
        ["Google Chrome", "主流浏览器", "更新到 141", "升级"],
        ["Mozilla Firefox", "独立内核浏览器", "未安装", "安装"],
        ["Everything", "本地文件搜索", "已安装", "安装"],
        ["PotPlayer", "本地视频播放器", "更新到 2509", "升级"],
        ["OBS Studio", "录屏与直播推流", "已安装", "安装"],
        ["UU远程", "低延迟远程控制", "未安装", "安装"]
      ].map(([name, description, status, action]) => `
        <article class="app-software-card">
          <div>
            <div class="app-software-name">${name}<span class="app-chip">推荐</span></div>
            <p>${description}</p>
          </div>
          <div class="app-software-foot"><span class="app-status ${status === "未安装" ? "off" : ""}">${status}</span>${pageButton(action, "small")}</div>
        </article>`).join("")}
    </div>
    <section class="app-card" style="margin-top:16px">
      <div class="app-card-head"><div><h3>自定义来源</h3><p>清单之外的软件可以填写 winget ID，并作为卡片加入本页。</p></div>${pageButton("添加")}</div>
      <div class="app-divider"></div>
      <div class="app-grid cols-2">
        <input class="app-input" aria-label="winget ID" placeholder="winget ID，例如 Tencent.QQ.NT">
        <input class="app-input" aria-label="显示名称" placeholder="显示名称">
      </div>
    </section>
  </div>`;

const renderPresets = () => `
  <div class="app-page">
    ${pageHeader("presets", pageButton("应用所选预设", "primary"))}
    <div class="app-status-line">4 套预设 · 执行前会生成快照并展示变更清单</div>
    <div class="app-grid cols-2">
      ${[
        ["01", "极简清爽", "关闭推荐内容、减少动画、整理任务栏，适合日常轻量系统。", "18 项变更"],
        ["02", "游戏优先", "关闭 Game DVR，启用游戏模式与 GPU 调度，保留必要安全策略。", "12 项变更"],
        ["03", "隐私优先", "关闭诊断数据、广告标识、活动历史和开始菜单推荐。", "15 项变更"],
        ["04", "开发环境", "打开开发者相关功能，调整资源管理器，保留 Windows 安全更新。", "20 项变更"]
      ].map(([number, title, description, count]) => `
        <article class="app-preset-card">
          <span class="number">${number} / PRESET</span><h3>${title}</h3><p>${description}</p>
          <footer><span>${count}</span>${pageButton("查看方案", "small")}</footer>
        </article>`).join("")}
    </div>
  </div>`;

const renderCleanup = () => `
  <div class="app-page">
    ${pageHeader("cleanup", `${pageButton("重新扫描")}${pageButton("开始清理", "primary")}`)}
    <div class="app-card">
      <div class="app-card-head"><div><h3>可清理空间</h3><p>扫描结果只用于演示，不会读取或删除本机文件。</p></div><strong style="font-size:28px">4.82 GB</strong></div>
      <div class="app-divider"></div>
      ${listRows([
        ["用户临时文件", "应用缓存、安装临时文件", "3.14 GB"],
        ["Windows 临时文件", "系统更新缓存与错误报告", "1.36 GB"],
        ["缩略图与图标缓存", "可自动重新生成的资源管理器缓存", "326 MB"]
      ])}
    </div>
    <p class="app-footer-note">清理操作不可回滚。部分正在被占用的文件会自动跳过。</p>
  </div>`;

const renderExecution = () => `
  <div class="app-page">
    ${pageHeader("execution")}
    <div class="app-status-line">20 个系统封装可用 · 执行前仍需检查计划与风险</div>
    <section class="app-card">
      <div class="app-toolbar">
        <span class="app-toolbar-label">START WITH</span>
        ${["空白工作流 · 0 步", "清爽新机 · 10 步", "隐私优先 · 5 步", "开发环境 · 10 步", "游戏优先 · 7 步"]
          .map((label) => `<button type="button" class="app-template" data-demo-action>${label}</button>`).join("")}
      </div>
    </section>
    <div class="app-tabs" style="margin-top:10px">
      <button type="button" class="app-tab ${activeTab === "builder" ? "is-active" : ""}" data-app-tab="builder">01 配置工作流</button>
      <button type="button" class="app-tab ${activeTab === "plan" ? "is-active" : ""}" data-app-tab="plan">02 计划与执行</button>
    </div>
    ${activeTab === "builder" ? `
      <div class="app-section-row"><div><h3>工作流配置</h3><p>把各模块配置成一个执行方案；发送后在同一页面查看计划、风险和报告</p></div><div class="app-page-actions">${pageButton("导入方案")}${pageButton("清空记录")}${pageButton("导出方案")}${pageButton("发送到执行", "primary")}</div></div>
      <section class="app-card">
        <h3 class="app-section-title">基本信息</h3><div class="app-divider"></div>
        <div class="app-form-grid">
          <label>方案名称</label><input class="app-input" value="我的开荒方案">
          <label>说明（可选）</label><input class="app-input" placeholder="描述这套方案的用途">
        </div>
        <div class="app-divider"></div>
        <div class="app-toggle-row"><div class="app-toggle-copy"><strong>完成后重启资源管理器</strong><p>右键菜单、任务栏与资源管理器外观设置需要重启后生效</p></div><button class="app-switch is-on" data-switch aria-checked="true" role="switch"></button></div>
      </section>
      <section class="app-card">
        <div class="app-card-head"><div><h3>步骤队列</h3><p>依赖排序后共 5 个步骤</p></div><span class="app-status">5 步</span></div>
        <div class="app-divider"></div>
        ${listRows([
          ["Windows 更新与右键菜单", "恢复经典右键菜单 · 暂停更新 7 天", "可回滚"],
          ["隐私策略", "关闭广告 ID、活动历史与推荐内容", "可回滚"],
          ["界面调优", "任务栏左对齐 · 关闭动画 · 时钟秒数", "可回滚"],
          ["启动项与服务", "禁用 4 个高影响启动项", "可回滚"],
          ["系统清理", "清理临时文件与缩略图缓存", "不可回滚", "warn"]
        ])}
      </section>` : `
      <div class="app-section-row"><div><h3>计划与执行</h3><p>依赖排序、风险预检和最终执行顺序</p></div><div class="app-page-actions">${pageButton("重新预检")}${pageButton("开始执行", "primary")}</div></div>
      <section class="app-card">
        <div class="app-card-head"><div><h3>执行计划</h3><p>5 个步骤 · 预计 2 分 40 秒</p></div><span class="app-status warn">1 个不可回滚项</span></div>
        <div class="app-divider"></div>
        ${listRows([
          ["01 · 创建配置快照", "备份注册表与当前策略", "就绪"],
          ["02 · 应用界面调优", "5 个注册表值", "就绪"],
          ["03 · 应用隐私策略", "6 个注册表值", "就绪"],
          ["04 · 调整启动项", "4 个启动项", "就绪"],
          ["05 · 清理临时文件", "预计释放 4.82 GB", "需确认", "warn"]
        ])}
      </section>`}
  </div>`;

const renderMigration = () => `
  <div class="app-page">
    ${pageHeader("migration", `${pageButton("重新扫描")}${pageButton("恢复克隆包")}${pageButton("生成克隆包 · 0", "primary")}`)}
    <div class="app-status-line">扫描完成：9 项已安装，9 项可选择，1 项缺少可迁移内容</div>
    <section class="app-notice">
      <span class="app-notice-icon">!</span>
      <div><strong>离线克隆边界</strong><p>安装器和开发工具目录会在源机器提取；目标机器无需访问官网。聊天记录只迁移指定数据目录，不包含登录令牌；商业许可证、驱动和机器绑定数据不会迁移。</p><p class="warn">克隆包可能包含私人聊天记录和下载文件，请存放在受保护的磁盘或加密容器中。</p></div>
    </section>
    <div class="app-migration-grid" style="margin-top:16px">
      ${[
        ["微信", "软件", "4.1.12.26", "已安装", "发现 2 个聊天数据目录，可离线迁移", "自动提取匹配版本的原厂安装器；导出时联网，目标机恢复不联网", "D:\\Program Files\\wx\\documents<br>%AppData%\\Tencent\\xwechat\\config", false],
        ["飞书", "软件", "8.4.7", "已安装", "发现 3 个聊天数据目录，可离线迁移", "已检测到本机原厂安装器，导出阶段不联网", "%AppData%\\LarkShell<br>%AppData%\\lark_public<br>%LocalAppData%\\Lark\\User Data", false],
        ["Git", "开发环境", "git version 2.55.0", "已安装", "安装目录：D:\\Program Files\\Git", "自动提取匹配版本的原厂安装器", "D:\\Program Files\\Git", false],
        ["Node.js", "开发环境", "v24.19.0", "已安装", "安装目录：C:\\Program Files\\nodejs", "自动提取匹配版本的原厂安装器", "C:\\Program Files\\nodejs", false],
        ["QQ", "软件", "9.9.36", "已安装", "未发现默认聊天数据目录", "添加有效数据目录后才可选择", "0 个数据目录", true]
      ].map(([name, kind, version, state, description, hint, paths, disabled]) => `
        <article class="app-migration-card ${disabled ? "is-disabled" : ""}">
          <div class="app-migration-top">
            <input class="app-check" type="checkbox" aria-label="${name}" ${disabled ? "disabled" : ""}>
            <div><div class="app-migration-title"><strong>${name}</strong><span class="app-chip">${kind}</span></div><small>${version}</small></div>
            <span class="app-status ${disabled ? "warn" : ""}">${disabled ? "不可选" : state}</span>
          </div>
          <p>${description}</p><p class="accent">${hint}</p><div class="app-migration-paths">${paths}</div>
          <div class="app-migration-actions">${pageButton("选择安装器目录", "small")}${pageButton("添加数据目录", "small")}</div>
        </article>`).join("")}
    </div>
  </div>`;

const renderSettings = () => `
  <div class="app-page">
    ${pageHeader("settings")}
    <section class="app-card">
      <div class="app-card-icon-title">${icon("sun")}<h3 class="app-section-title">外观与动效</h3></div>
      <div class="app-divider"></div>
      ${toggleRows([
        ["深色模式", "当前：浅色。只切换 FreshStart 自身主题，不修改 Windows 系统深色模式。", false],
        ["界面动效", "关闭后页面切换、开关与 Toast 动画立即完成（无障碍）。", true],
        ["独立任务控制台", "开启后使用独立终端窗口显示原生命令、输出和 exit code。", true]
      ])}
      <div class="app-divider"></div>
      <div class="app-toggle-row">
        <div class="app-toggle-copy"><strong>主题色</strong><p>影响按钮、链接与选中高亮；随主题一起保存</p></div>
        <div class="app-swatch-row">
          ${["#007AFF", "#34C759", "#FF9500", "#FF3B30", "#AF52DE", "#636366"]
            .map((color, index) => `<button class="app-swatch ${index === 0 ? "is-active" : ""}" type="button" style="--swatch:${color}" data-swatch="${color}" aria-label="切换到主题色 ${color}"></button>`).join("")}
        </div>
      </div>
      <div class="app-divider"></div>
      <strong style="font-size:13px">桌面壁纸</strong>
      <p class="app-card-subtitle">把内置图片设为 Windows 桌面壁纸，设置前会自动记住原来的壁纸。</p>
      <div class="app-wallpapers">
        <button class="app-wallpaper is-active" type="button" data-demo-action><img src="assets/wallpapers/clouds.jpg" alt="云雾壁纸"><span>云雾</span></button>
        <button class="app-wallpaper" type="button" data-demo-action><img src="assets/wallpapers/butterfly.jpg" alt="发光蝴蝶壁纸"><span>发光蝴蝶</span></button>
        <button class="app-wallpaper" type="button" data-demo-action><img src="assets/wallpapers/flower.jpg" alt="孤独花朵壁纸"><span>孤独花朵</span></button>
      </div>
    </section>
    <section class="app-card">
      <div class="app-card-icon-title">${icon("shield")}<h3 class="app-section-title">安全与备份</h3></div>
      <div class="app-divider"></div>
      ${toggleRows([["首次修改前询问系统还原点", "关闭后不再弹窗，所有写入仍会保留可回滚的 JSON 快照", true]])}
      <div class="app-toolbar">${pageButton("打开备份目录")}${pageButton("打开日志目录")}</div>
    </section>
    <section class="app-card">
      <div class="app-card-icon-title">${icon("info")}<h3 class="app-section-title">关于</h3></div>
      <div class="app-divider"></div>
      <p style="font-size:12.5px;margin:0">FreshStart 0.2.0 · Windows 11 · .NET 8 · WPF</p>
      <p class="app-footer-note" style="margin-top:8px">不采集、不上传任何用户数据；不内置广告与捆绑安装。所有修改都保存在本机，并生成可回滚的备份。</p>
    </section>
  </div>`;

const settingsPage = (key, iconName, rows, actions = `${pageButton("刷新")}${pageButton("应用更改", "primary")}`) => `
  <div class="app-page">
    ${pageHeader(key, actions)}
    <div class="app-status-line">当前界面为交互预览，不会应用任何系统设置</div>
    <section class="app-card">
      <div class="app-card-icon-title">${icon(iconName)}<h3 class="app-section-title">${pageMeta[key][0]}配置</h3></div>
      <div class="app-divider"></div>
      ${toggleRows(rows)}
    </section>
    <p class="app-footer-note">页面布局、状态与组件样式按 FreshStart v0.2.0 当前 XAML 复刻。</p>
  </div>`;

const renderPerformance = () => `
  <div class="app-page">
    ${pageHeader("performance", `<span class="app-toolbar-label">采样</span>${["1s", "2s", "5s"].map((v, i) => `<button class="app-template ${i === 0 ? "is-active" : ""}" data-demo-action>${v}</button>`).join("")}`)}
    <section class="app-card">
      <div class="app-card-head"><h3 class="app-section-title">实时指标</h3><span class="app-status">刚刚更新</span></div>
      <div class="app-divider"></div>
      ${[
        ["CPU", "18%", "M2 27 C30 8 55 31 82 18 S138 22 170 10 S226 14 260 7 S310 19 340 12"],
        ["内存", "31.2 GB / 64 GB", "M2 20 C32 20 58 17 82 21 S138 18 170 22 S226 16 260 19 S310 17 340 20"],
        ["磁盘", "46 MB/s", "M2 25 C30 28 58 22 82 30 S138 12 170 25 S226 24 260 14 S310 20 340 8"],
        ["网络", "8.4 Mbps", "M2 24 C30 15 58 27 82 4 S138 28 170 19 S226 27 260 10 S310 25 340 11"],
        ["GPU", "12%", "M2 29 C30 24 58 28 82 23 S138 11 170 21 S226 18 260 6 S310 12 340 4"]
      ].map(([label, value, path]) => `<div class="app-metric-row"><span>${label}</span><strong>${value}</strong><svg class="app-sparkline" viewBox="0 0 342 34" preserveAspectRatio="none"><path d="${path}"></path></svg></div>`).join("")}
    </section>
    <section class="app-card">
      <div class="app-card-head"><div><h3>进程</h3><p>按占用排序；结束进程不可撤销，请谨慎操作</p></div><div class="app-toolbar">${["CPU", "内存", "名称"].map((x, i) => `<button class="app-template ${i === 0 ? "is-active" : ""}" data-demo-action>${x}</button>`).join("")}</div></div>
      <div class="app-divider"></div>
      ${[
        ["Codex", "PID 20052", "4.8%", "1.2 GB"], ["explorer", "PID 4328", "2.1%", "284 MB"],
        ["Feishu", "PID 9120", "1.3%", "820 MB"], ["dwm", "PID 1176", "0.9%", "198 MB"],
        ["SearchHost", "PID 7640", "0.3%", "142 MB"]
      ].map(([name, pid, cpu, memory]) => `<div class="app-process-row"><span><strong>${name}</strong><small>${pid}</small></span><span>${cpu}</span><span>${memory}</span>${pageButton("结束", "small danger")}</div>`).join("")}
    </section>
  </div>`;

const renderNetwork = () => `
  <div class="app-page">
    ${pageHeader("network", `${pageButton("刷新")}${pageButton("应用 DNS", "primary")}`)}
    <div class="app-status-line">当前网络：以太网 · 192.168.1.42 · 已连接</div>
    <div class="app-grid cols-2">
      ${infoCard("当前适配器", "network", [["接口", "Realtek 2.5GbE"], ["网关", "192.168.1.1"], ["DNS", "223.5.5.5 · 119.29.29.29"], ["链路速度", "2.5 Gbps"]])}
      <section class="app-card"><div class="app-card-icon-title">${icon("network")}<h3 class="app-section-title">常用 DNS</h3></div><div class="app-divider"></div>
        ${listRows([["自动获取", "由 DHCP 提供 DNS 地址", "当前"], ["阿里云 DNS", "223.5.5.5 · 223.6.6.6", "可用"], ["腾讯 DNS", "119.29.29.29 · 182.254.116.116", "可用"], ["Cloudflare", "1.1.1.1 · 1.0.0.1", "可用"]])}
      </section>
    </div>
  </div>`;

const renderWindowsUpdate = () => `
  <div class="app-page">
    ${pageHeader("windows-update", pageButton("刷新"))}
    <div class="app-grid cols-2">
      <section class="app-card"><div class="app-card-head"><div><h3>Windows Update 服务</h3><p>当前由 Windows 自动管理</p></div><span class="app-status">运行中</span></div><div class="app-divider"></div><div class="app-toolbar">${pageButton("暂停 7 天")}${pageButton("延长至 2099")}${pageButton("恢复默认", "primary")}</div></section>
      <section class="app-card"><h3 class="app-section-title">最近安装</h3><div class="app-divider"></div>${listRows([["2026-11 累积更新", "适用于 Windows 11 24H2", "已安装"], [".NET 安全更新", "KB5064401", "已安装"], ["驱动质量更新", "系统组件与固件", "已安装"]])}</section>
    </div>
    <p class="app-footer-note">彻底关闭更新属于不可自动回滚操作，正式软件会在执行前显示风险说明并请求确认。</p>
  </div>`;

const tablePage = (key, iconName, columns, rows, actions) => `
  <div class="app-page">
    ${pageHeader(key, actions)}
    <div class="app-search-row"><input class="app-input" placeholder="搜索名称、发布者或描述"><button class="app-btn">筛选</button></div>
    <section class="app-card">
      <div class="app-card-icon-title">${icon(iconName)}<h3 class="app-section-title">${pageMeta[key][0]}列表</h3></div><div class="app-divider"></div>
      <table class="app-table"><thead><tr>${columns.map((c) => `<th>${c}</th>`).join("")}</tr></thead><tbody>
        ${rows.map((row) => `<tr>${row.map((cell, index) => `<td>${index === row.length - 1 ? `<span class="app-status">${cell}</span>` : cell}</td>`).join("")}</tr>`).join("")}
      </tbody></table>
    </section>
  </div>`;

const renderPage = (key) => {
  switch (key) {
    case "home": return renderHome();
    case "system-info": return renderSystemInfo();
    case "software": return renderSoftware();
    case "presets": return renderPresets();
    case "cleanup": return renderCleanup();
    case "execution": return renderExecution();
    case "migration": return renderMigration();
    case "settings": return renderSettings();
    case "performance": return renderPerformance();
    case "network": return renderNetwork();
    case "windows-update": return renderWindowsUpdate();
    case "context-menu": return settingsPage(key, "context-menu", [["Windows 11 新版右键菜单", "保持系统默认的紧凑菜单", true], ["经典右键菜单", "恢复 Windows 10 风格完整菜单", false], ["显示“发送到”", "在右键菜单中显示发送到子菜单", true], ["显示“复制为路径”", "为文件和文件夹添加复制路径命令", true]]);
    case "ui-tuning": return settingsPage(key, "sliders", [["任务栏左对齐", "将开始按钮和任务栏图标移到左侧", true], ["显示时钟秒数", "在任务栏时钟中显示秒", false], ["窗口透明度", "保留 Windows 视觉透明度", true], ["任务栏小组件", "显示或隐藏小组件入口", false], ["显示最近文件", "在资源管理器中显示最近使用内容", false], ["游戏栏控制器提示", "连接手柄时显示游戏栏提示", false]]);
    case "privacy": return settingsPage(key, "privacy", [["诊断数据", "限制发送可选诊断数据", true], ["广告 ID", "禁止应用使用广告标识", true], ["活动历史", "停止收集本地活动记录", true], ["在线语音识别", "关闭云端语音识别", false], ["开始菜单推荐", "隐藏推荐内容与建议", true], ["定制体验", "关闭基于使用习惯的个性化提示", true], ["消费级推送", "限制 Microsoft 账户相关推送", false]]);
    case "defender": return settings_page_defender();
    case "features": return tablePage(key, "apps", ["功能", "状态", "说明"], [["Hyper-V", "已启用", "虚拟化平台和管理工具"], ["Windows Subsystem for Linux", "已启用", "运行 Linux 发行版"], [".NET Framework 3.5", "未启用", "兼容旧版桌面应用"], ["OpenSSH Server", "未启用", "远程 SSH 服务"], ["Windows 沙盒", "已启用", "隔离的临时桌面环境"]], pageButton("刷新"));
    case "game-optimize": return settingsPage(key, "game", [["游戏模式", "优先为游戏分配 CPU 和 GPU 资源", true], ["Game DVR 后台录制", "允许在后台捕获游戏片段", false], ["游戏栏控制器提示", "连接手柄时显示快捷提示", false], ["硬件加速 GPU 调度", "启用 HAGS 调度策略", true]]);
    case "appx": return tablePage(key, "apps", ["应用", "版本", "类型", "状态"], [["Windows 照片", "26.1001", "系统应用", "已安装"], ["Terminal", "1.22.1", "用户应用", "已安装"], ["Microsoft Store", "22610", "系统应用", "已安装"], ["剪映", "11.7.0", "用户应用", "已安装"]], `${pageButton("刷新")}${pageButton("重新注册", "primary")}`);
    case "uninstall": return tablePage(key, "uninstall", ["软件", "发布者", "大小", "状态"], [["飞书", "Beijing Feishu", "1.54 GB", "已安装"], ["Google Chrome", "Google LLC", "612 MB", "已安装"], ["7-Zip", "Igor Pavlov", "5.4 MB", "已安装"], ["PotPlayer", "Kakao", "128 MB", "已安装"]], `${pageButton("刷新")}${pageButton("导出列表", "primary")}`);
    case "startup": return tablePage(key, "startup", ["启动项", "来源", "影响", "状态"], [["Feishu", "注册表 Run", "中", "已启用"], ["Everything", "计划任务", "低", "已启用"], ["OneDrive", "注册表 Run", "中", "已启用"], ["Adobe GC Invoker", "启动文件夹", "高", "已禁用"]], pageButton("刷新"));
    case "service-tasks": return tablePage(key, "gear", ["名称", "显示名称", "启动类型", "状态"], [["wuauserv", "Windows Update", "手动", "运行中"], ["Spooler", "Print Spooler", "自动", "运行中"], ["DiagTrack", "Connected User Experiences", "禁用", "已停止"], ["SysMain", "SysMain", "自动", "运行中"]], `${pageButton("刷新")}${pageButton("应用更改", "primary")}`);
    case "power": return settingsPage(key, "power", [["快速启动", "缩短关机后再次启动的时间", true], ["合盖时睡眠", "笔记本电脑合盖后进入睡眠", true], ["节能模式", "电量较低时降低后台活动", false]]);
    case "driver": return tablePage(key, "chip", ["设备", "驱动版本", "发布者", "状态"], [["NVIDIA GeForce RTX 4080", "581.15", "NVIDIA", "正常"], ["Realtek 2.5GbE", "10.74", "Realtek", "正常"], ["Intel Wi-Fi 7", "23.90", "Intel", "正常"], ["USB Audio", "10.0.26100", "Microsoft", "正常"]], `${pageButton("导出全部")}${pageButton("刷新")}`);
    case "restore": return `
      <div class="app-page">${pageHeader(key, `${pageButton("创建基准")}${pageButton("恢复基准", "primary")}`)}
      <section class="app-card"><div class="app-card-head"><div><h3>系统基准</h3><p>首次运行 FreshStart 时记录的 26 项配置</p></div><span class="app-status">可恢复</span></div><div class="app-divider"></div>${listRows([["注册表配置", "26 项基线值", "已记录"], ["服务状态", "启动类型与运行状态", "已记录"], ["计划任务", "可用状态", "已记录"], ["最近变更", "2 项值与基准不同", "需注意", "warn"]])}</section>
      </div>`;
    case "history": return tablePage(key, "activity", ["时间", "操作", "影响", "结果"], [["14:21", "刷新 winget 源索引", "用户配置", "成功"], ["14:12", "恢复飞书克隆包", "聊天数据", "成功"], ["13:58", "应用隐私策略", "5 项注册表", "可回滚"], ["13:44", "清理临时文件", "4.82 GB", "成功"]], pageButton("刷新"));
    default: return `<div class="app-page">${pageHeader(key)}<div class="app-card"><p class="app-card-subtitle">该页面为产品界面预览。</p></div></div>`;
  }
};

function settings_page_defender() {
  return `
    <div class="app-page">
      ${pageHeader("defender", `${pageButton("刷新")}${pageButton("打开 Windows 安全中心", "primary")}`)}
      <div class="app-grid cols-2">
        <section class="app-card"><div class="app-card-head"><div><h3>Microsoft Defender</h3><p>当前状态正常</p></div><span class="app-status">受保护</span></div><div class="app-divider"></div>${toggleRows([["实时防护", "持续扫描下载、安装与运行的应用", true], ["云保护", "向 Microsoft 发送可疑文件元数据", true], ["篡改保护", "阻止未经授权的安全设置更改", true]])}</section>
        <section class="app-card"><div class="app-card-head"><div><h3>防火墙</h3><p>域、专用和公用网络配置</p></div><span class="app-status">已启用</span></div><div class="app-divider"></div>${listRows([["域网络", "Microsoft Defender Firewall", "已启用"], ["专用网络", "Microsoft Defender Firewall", "已启用"], ["公用网络", "Microsoft Defender Firewall", "已启用"]])}</section>
      </div>
    </div>`;
}

const renderNavigation = (query = "") => {
  const normalized = query.trim().toLocaleLowerCase("zh-CN");
  let count = 0;
  appNav.innerHTML = navGroups.map((group) => {
    const items = group.items.filter((item) =>
      !normalized
      || item.title.toLocaleLowerCase("zh-CN").includes(normalized)
      || item.hint.toLocaleLowerCase("zh-CN").includes(normalized)
      || group.title.toLocaleLowerCase("zh-CN").includes(normalized)
    );
    if (!items.length) return "";
    count += items.length;
    return `
      <section class="app-nav-group">
        <div class="app-nav-group-title">${group.title}</div>
        ${items.map((item) => `
          <button type="button" class="app-nav-item ${item.key === activePage ? "is-active" : ""}" data-page="${item.key}" title="${item.hint}">
            <span class="app-nav-icon">${icon(item.iconName)}</span>
            <span class="app-nav-label">${item.title}</span>
          </button>`).join("")}
      </section>`;
  }).join("");
  if (!count) appNav.innerHTML = '<p class="app-nav-empty">没有匹配的页面</p>';
};

const showToast = (message) => {
  if (!appToast) return;
  clearTimeout(toastTimer);
  appToast.textContent = message;
  appToast.classList.add("is-visible");
  toastTimer = setTimeout(() => appToast.classList.remove("is-visible"), 2600);
};

const navigate = (key) => {
  if (!pageMeta[key]) return;
  activePage = key;
  appTitle.textContent = pageMeta[key][0];
  appView.innerHTML = renderPage(key);
  appView.scrollTop = 0;
  renderNavigation(appSearch?.value ?? "");
};

appNav?.addEventListener("click", (event) => {
  const button = event.target.closest("[data-page]");
  if (!button) return;
  navigate(button.dataset.page);
});

appSearch?.addEventListener("input", () => renderNavigation(appSearch.value));

appView?.addEventListener("click", (event) => {
  const tab = event.target.closest("[data-app-tab]");
  if (tab) {
    activeTab = tab.dataset.appTab;
    appView.innerHTML = renderPage(activePage);
    appView.scrollTop = 0;
    return;
  }
  const toggle = event.target.closest("[data-switch]");
  if (toggle) {
    const enabled = toggle.classList.toggle("is-on");
    toggle.setAttribute("aria-checked", String(enabled));
    showToast("这是界面预览：开关状态不会保存，也不会修改系统。");
    return;
  }
  const swatch = event.target.closest("[data-swatch]");
  if (swatch) {
    appView.querySelectorAll("[data-swatch]").forEach((item) => item.classList.remove("is-active"));
    swatch.classList.add("is-active");
    appWindow.style.setProperty("--app-accent", swatch.dataset.swatch);
    showToast("已预览主题色效果；官网不会写入 FreshStart 设置。");
    return;
  }
  if (event.target.closest("[data-demo-action]")) {
    showToast("这是 FreshStart 官网交互预览，不会执行任何系统功能。");
  }
});

themeButton?.addEventListener("click", () => {
  const dark = appWindow.dataset.theme !== "dark";
  appWindow.dataset.theme = dark ? "dark" : "light";
  themeButton.textContent = dark ? "☀" : "☾";
  themeButton.setAttribute("aria-label", dark ? "切换到浅色主题" : "切换到深色主题");
  showToast(dark ? "已切换为深色模式预览" : "已切换为浅色模式预览");
});

sidebarButton?.addEventListener("click", () => {
  const collapsed = appWindow.classList.toggle("is-sidebar-collapsed");
  sidebarButton.textContent = collapsed ? "›" : "‹";
  sidebarButton.title = collapsed ? "展开侧边栏" : "收起侧边栏";
});

document.querySelectorAll("[data-open-page]").forEach((button) => {
  button.addEventListener("click", () => {
    navigate(button.dataset.openPage);
    document.querySelector("#experience")?.scrollIntoView({ behavior: prefersReducedMotion ? "auto" : "smooth" });
  });
});

const initialParams = new URLSearchParams(window.location.search);
const initialPage = initialParams.get("tour");
const initialTheme = initialParams.get("theme");
if (initialTheme === "dark") {
  appWindow.dataset.theme = "dark";
  themeButton.textContent = "☀";
  themeButton.setAttribute("aria-label", "切换到浅色主题");
}
renderNavigation();
navigate(pageMeta[initialPage] ? initialPage : "home");
