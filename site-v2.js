(() => {
  const body = document.body;
  const header = document.querySelector("[data-site-header]");
  const menuToggle = document.querySelector("[data-menu-toggle]");
  const mobileMenu = document.querySelector("[data-mobile-menu]");
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const suiteStage = document.querySelector("[data-suite-stage]");
  const suiteImage = document.querySelector("[data-suite-image]");
  const suiteTitle = document.querySelector("[data-suite-title]");
  const suiteCopy = document.querySelector("[data-suite-copy]");
  const suiteTags = document.querySelector("[data-suite-tags]");
  const suiteNumber = document.querySelector("[data-suite-number]");
  const suiteIndex = document.querySelector("[data-suite-index]");
  const suiteTabs = [...document.querySelectorAll("[data-suite-tab]")];
  let activeSuiteKey = "";
  const backgroundMusic = document.querySelector("[data-background-music]");
  const audioToggles = [...document.querySelectorAll("[data-audio-toggle]")];
  const musicStorageKey = "freshstart:background-music";
  const musicVolume = 0.18;
  let musicEnabled = true;
  let musicStarted = false;
  let musicFadeFrame = 0;

  try {
    musicEnabled = window.localStorage.getItem(musicStorageKey) !== "off";
  } catch {
    musicEnabled = true;
  }

  const updateAudioControls = () => {
    audioToggles.forEach((button) => {
      button.setAttribute("aria-pressed", String(musicEnabled));
      button.setAttribute("aria-label", musicEnabled ? "关闭背景音乐" : "开启背景音乐");
      button.classList.toggle("is-waiting", musicEnabled && !musicStarted);
      button.classList.toggle("is-playing", musicEnabled && musicStarted);
    });
  };

  const fadeMusic = (targetVolume, finish) => {
    if (!backgroundMusic) return;
    cancelAnimationFrame(musicFadeFrame);
    const from = backgroundMusic.volume;
    const startedAt = performance.now();
    const duration = 1300;
    const step = (now) => {
      const progress = Math.min(1, (now - startedAt) / duration);
      backgroundMusic.volume = from + (targetVolume - from) * progress;
      if (progress < 1) {
        musicFadeFrame = requestAnimationFrame(step);
      } else {
        finish?.();
      }
    };
    musicFadeFrame = requestAnimationFrame(step);
  };

  const startMusic = async () => {
    if (!backgroundMusic || !musicEnabled) return;
    cancelAnimationFrame(musicFadeFrame);
    if (!backgroundMusic.paused) {
      musicStarted = true;
      updateAudioControls();
      fadeMusic(musicVolume);
      return;
    }
    backgroundMusic.volume = 0;
    try {
      await backgroundMusic.play();
      musicStarted = true;
      updateAudioControls();
      fadeMusic(musicVolume);
    } catch {
      musicStarted = false;
      updateAudioControls();
    }
  };

  const stopMusic = () => {
    if (!backgroundMusic) return;
    fadeMusic(0, () => {
      backgroundMusic.pause();
      musicStarted = false;
      updateAudioControls();
    });
  };

  const toggleMusic = () => {
    musicEnabled = !musicEnabled;
    try {
      window.localStorage.setItem(musicStorageKey, musicEnabled ? "on" : "off");
    } catch {}
    if (musicEnabled) {
      updateAudioControls();
      startMusic();
    } else {
      stopMusic();
    }
  };

  audioToggles.forEach((button) => button.addEventListener("click", toggleMusic));
  updateAudioControls();

  const startMusicAfterGesture = (event) => {
    if (
      event.target instanceof Element
      && event.target.closest("[data-audio-toggle]")
    ) {
      return;
    }
    if (musicEnabled && !musicStarted) startMusic();
  };

  ["pointerdown", "keydown", "touchstart", "wheel"].forEach((eventName) => {
    document.addEventListener(eventName, startMusicAfterGesture, {
      capture: true,
      passive: true,
      once: true
    });
  });

  const preloader = document.querySelector("[data-preloader]");
  const startMusicWhenLetterAppears = () => {
    if (preloader?.classList.contains("is-folding")) {
      startMusic();
      musicObserver?.disconnect();
    }
  };
  const musicObserver = preloader
    ? new MutationObserver(startMusicWhenLetterAppears)
    : null;
  musicObserver?.observe(preloader, { attributes: true, attributeFilter: ["class"] });
  startMusicWhenLetterAppears();

  if (!preloader && document.documentElement.classList.contains("has-preloaded")) {
    window.setTimeout(startMusic, 180);
  }

  document.addEventListener("visibilitychange", () => {
    if (!backgroundMusic || !musicStarted) return;
    if (document.hidden) {
      backgroundMusic.pause();
    } else if (musicEnabled) {
      startMusic();
    }
  });

  const suiteData = {
    overview: {
      src: "site-assets/home-dark.png",
      number: "01 / OVERVIEW",
      title: "开荒工作台",
      copy: "把系统状态、常用操作和下一步推荐集中在一个清晰界面里。所有功能都从同一个本地工作台出发。",
      tags: ["系统评分", "快捷入口", "本地运行"]
    },
    system: {
      src: "site-assets/system-light.png",
      number: "02 / SYSTEM",
      title: "先看清机器，再决定改变",
      copy: "CPU、内存、主板、显卡、磁盘、网络和系统版本建立完整基线，不需要在多个系统面板之间来回查找。",
      tags: ["硬件基线", "激活状态", "只读扫描"]
    },
    software: {
      src: "site-assets/software-light.png",
      number: "03 / SOFTWARE",
      title: "安装软件，也安装秩序",
      copy: "常用软件安装、升级、卸载和残留清理共用同一条可追踪链路，winget 不可用时仍保留官方安装方案。",
      tags: ["winget", "官方回退", "安装记录"]
    },
    settings: {
      src: "site-assets/settings-light.png",
      number: "04 / SETTINGS",
      title: "把细节调到自己的习惯",
      copy: "界面、动效、备份策略、启动行为与权限偏好集中管理，不把关键开关埋在系统深处。",
      tags: ["主题", "备份策略", "动效"]
    },
    automation: {
      src: "site-assets/execution-dark.png",
      number: "05 / AUTOMATION",
      title: "每一步执行都有清楚后果",
      copy: "把多个系统动作编排成工作流，执行前检查风险与依赖，执行后保留输出、退出码和可回顾的报告。",
      tags: ["工作流", "风险预检", "执行报告"]
    }
  };

  const setSuite = (key, focus = false) => {
    const data = suiteData[key];
    if (!data || !suiteImage) return;
    const changed = activeSuiteKey !== key;
    activeSuiteKey = key;
    const tabIndex = suiteTabs.findIndex((tab) => tab.dataset.suiteTab === key);
    if (suiteIndex && tabIndex >= 0) {
      suiteIndex.textContent = `${String(tabIndex + 1).padStart(2, "0")} / 05`;
    }

    suiteTabs.forEach((tab) => {
      const active = tab.dataset.suiteTab === key;
      tab.classList.toggle("is-active", active);
      tab.setAttribute("aria-selected", String(active));
      tab.tabIndex = active ? 0 : -1;
      if (active && focus) tab.focus();
    });

    if (!changed) return;
    if (suiteStage) suiteStage.classList.add("is-switching");
    window.setTimeout(() => {
      suiteImage.src = data.src;
      suiteImage.alt = `${data.title}功能界面`;
      if (suiteTitle) suiteTitle.textContent = data.title;
      if (suiteCopy) suiteCopy.textContent = data.copy;
      if (suiteNumber) suiteNumber.textContent = data.number;
      if (suiteTags) {
        suiteTags.innerHTML = data.tags.map((tag) => `<span>${tag}</span>`).join("");
      }
      suiteStage?.classList.remove("is-switching");
    }, reducedMotion ? 0 : 150);
  };

  suiteTabs.forEach((tab, index) => {
    tab.addEventListener("click", () => setSuite(tab.dataset.suiteTab));
    tab.addEventListener("keydown", (event) => {
      if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return;
      event.preventDefault();
      const direction = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
      const next = (index + direction + suiteTabs.length) % suiteTabs.length;
      setSuite(suiteTabs[next].dataset.suiteTab, true);
    });
  });

  const closeMenu = () => {
    body.classList.remove("n-menu-open");
    menuToggle?.setAttribute("aria-expanded", "false");
  };

  menuToggle?.addEventListener("click", () => {
    const open = !body.classList.contains("n-menu-open");
    body.classList.toggle("n-menu-open", open);
    menuToggle.setAttribute("aria-expanded", String(open));
  });

  mobileMenu?.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", closeMenu);
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeMenu();
  });

  let lastScroll = window.scrollY;
  const updateHeader = () => {
    const current = window.scrollY;
    header?.classList.toggle("is-scrolled", current > 12);
    header?.classList.toggle("is-hidden", current > 240 && current > lastScroll);
    lastScroll = current;
  };

  window.addEventListener("scroll", updateHeader, { passive: true });
  updateHeader();

  if ("IntersectionObserver" in window && !reducedMotion) {
    const revealObserver = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          entry.target.classList.add("is-visible");
          revealObserver.unobserve(entry.target);
        });
      },
      { threshold: 0.12, rootMargin: "0px 0px -8% 0px" }
    );
    document.querySelectorAll(".n-reveal").forEach((element) => revealObserver.observe(element));
  } else {
    document.querySelectorAll(".n-reveal").forEach((element) => element.classList.add("is-visible"));
  }

  document.querySelectorAll(".n-faq-list details").forEach((details) => {
    details.addEventListener("toggle", () => {
      if (!details.open) return;
      document.querySelectorAll(".n-faq-list details").forEach((other) => {
        if (other !== details) other.open = false;
      });
    });
  });

  const hero = document.querySelector("[data-hero]");
  const heroTitle = document.querySelector("[data-hero-title]");
  const heroArt = document.querySelector("[data-hero-art]");
  const heroWindow = document.querySelector(".n-hero-window");
  const cubeStack = document.querySelector("[data-cube-stack]");
  const suiteScene = document.querySelector("[data-suite-scene]");
  const suiteBrowser = document.querySelector("[data-suite-browser]");
  const scrollProgress = document.querySelector("[data-scroll-progress]");
  const interfaceCards = [...document.querySelectorAll(".n-interface-card")];
  const handoffMedia = document.querySelector("[data-parallax-media]");
  let pointerFrame = 0;
  let scrollFrame = 0;

  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

  const updatePointer = (event) => {
    if (!hero || reducedMotion) return;
    const bounds = hero.getBoundingClientRect();
    const x = (event.clientX - bounds.left) / bounds.width;
    const y = (event.clientY - bounds.top) / bounds.height;
    if (pointerFrame) cancelAnimationFrame(pointerFrame);
    pointerFrame = requestAnimationFrame(() => {
      body.style.setProperty("--pointer-x", `${event.clientX}px`);
      body.style.setProperty("--pointer-y", `${event.clientY}px`);
      if (heroArt) {
        heroArt.style.setProperty("--hero-pointer-x", `${(x - 0.5) * 18}px`);
        heroArt.style.setProperty("--hero-pointer-y", `${(y - 0.5) * 14}px`);
      }
      if (cubeStack) {
        cubeStack.style.setProperty("--cube-pointer-x", `${(x - 0.5) * -28}px`);
        cubeStack.style.setProperty("--cube-pointer-y", `${(y - 0.5) * -22}px`);
      }
    });
  };

  hero?.addEventListener("pointermove", updatePointer, { passive: true });

  const updateScrollEffects = () => {
    scrollFrame = 0;
    const y = window.scrollY;
    const viewport = window.innerHeight;
    const maxScroll = Math.max(1, document.documentElement.scrollHeight - viewport);

    if (scrollProgress) {
      scrollProgress.style.transform = `scaleX(${clamp(y / maxScroll, 0, 1)})`;
    }

    if (hero && heroTitle) {
      const progress = clamp(y / Math.max(1, hero.offsetHeight * 0.72), 0, 1);
      heroTitle.style.setProperty("--hero-title-x", `${progress * -3.5}vw`);
      heroTitle.style.setProperty("--hero-title-y", `${progress * -70}px`);
      heroTitle.style.setProperty("--hero-title-scale", String(1 - progress * 0.06));
      heroArt?.style.setProperty("--hero-shift-y", `${progress * -110}px`);
      heroArt?.style.setProperty("--hero-shift-x", `${progress * 60}px`);
      heroWindow?.style.setProperty("--hero-window-scale", String(1 + progress * 0.055));
      heroWindow?.style.setProperty("--hero-window-rotate", `${-4 + progress * 2.8}deg`);
      cubeStack?.style.setProperty("--cube-shift-y", `${progress * -145}px`);
    }

    if (suiteScene && suiteBrowser) {
      const rect = suiteScene.getBoundingClientRect();
      const range = Math.max(1, suiteScene.offsetHeight - viewport);
      const progress = clamp(-rect.top / range, 0, 1);
      const tilt = (progress - 0.5) * 2.4;
      suiteBrowser.style.setProperty("--suite-tilt", `${tilt}deg`);
      suiteBrowser.style.setProperty("--suite-lift", `${Math.sin(progress * Math.PI) * -8}px`);

      if (window.innerWidth > 1120 && suiteTabs.length && !reducedMotion) {
        const nextIndex = clamp(Math.floor(progress * suiteTabs.length), 0, suiteTabs.length - 1);
        const nextKey = suiteTabs[nextIndex]?.dataset.suiteTab;
        if (nextKey && nextKey !== activeSuiteKey) setSuite(nextKey);
      }
    }

    interfaceCards.forEach((card, index) => {
      const rect = card.getBoundingClientRect();
      const distance = (rect.top + rect.height / 2 - viewport / 2) / viewport;
      const drift = clamp(distance, -1.2, 1.2) * (index % 2 === 0 ? -18 : 22);
      card.style.setProperty("--drift-y", `${drift}px`);
      card.style.setProperty("--drift-r", `${drift * 0.015}deg`);
    });

    if (handoffMedia) {
      const rect = handoffMedia.getBoundingClientRect();
      const progress = clamp((viewport - rect.top) / (viewport + rect.height), 0, 1);
      handoffMedia.style.setProperty("--media-y", `${(0.5 - progress) * 70}px`);
    }
  };

  const requestScrollEffects = () => {
    if (scrollFrame) return;
    scrollFrame = requestAnimationFrame(updateScrollEffects);
  };

  window.addEventListener("scroll", requestScrollEffects, { passive: true });
  window.addEventListener("resize", requestScrollEffects, { passive: true });

  const supportsInertialScroll =
    !reducedMotion
    && window.innerWidth > 1024
    && window.matchMedia("(hover: hover) and (pointer: fine)").matches;

  if (supportsInertialScroll) {
    let targetScroll = window.scrollY;
    let inertialFrame = 0;
    let animating = false;
    let lastRenderedScroll = window.scrollY;

    const cancelInertia = () => {
      if (inertialFrame) cancelAnimationFrame(inertialFrame);
      inertialFrame = 0;
      animating = false;
      targetScroll = window.scrollY;
      lastRenderedScroll = targetScroll;
    };

    const animateScroll = () => {
      const current = window.scrollY;
      if (Math.abs(current - lastRenderedScroll) > 2) {
        cancelInertia();
        return;
      }
      const distance = targetScroll - current;
      if (Math.abs(distance) < 0.6) {
        window.scrollTo(0, targetScroll);
        animating = false;
        inertialFrame = 0;
        return;
      }
      lastRenderedScroll = current + distance * 0.3;
      window.scrollTo(0, lastRenderedScroll);
      inertialFrame = requestAnimationFrame(animateScroll);
    };

    window.addEventListener(
      "wheel",
      (event) => {
        if (event.ctrlKey || body.classList.contains("n-menu-open")) return;
        if (document.documentElement.classList.contains("is-preloading")) return;
        const multiplier = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? window.innerHeight : 1;
        const max = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
        targetScroll = clamp(targetScroll + event.deltaY * multiplier * 13, 0, max);
        event.preventDefault();
        if (!animating) {
          animating = true;
          inertialFrame = requestAnimationFrame(animateScroll);
        }
      },
      { passive: false }
    );

    document.addEventListener(
      "pointerdown",
      (event) => {
        if (
          event.target instanceof Element
          && event.target.closest("a, button, input, summary")
        ) {
          cancelInertia();
        }
      },
      { passive: true }
    );

    document.addEventListener("keydown", (event) => {
      if (
        ["ArrowDown", "ArrowUp", "PageDown", "PageUp", "Home", "End", " "].includes(event.key)
      ) {
        cancelInertia();
      }
    });

    window.addEventListener(
      "scroll",
      () => {
        if (!animating) targetScroll = window.scrollY;
      },
      { passive: true }
    );
  }

  const year = document.querySelector("[data-year]");
  if (year) year.textContent = String(new Date().getFullYear());

  const sectionLinks = [...document.querySelectorAll(".n-nav a[href^='#']")];
  const sections = sectionLinks
    .map((link) => document.querySelector(link.getAttribute("href")))
    .filter(Boolean);

  if ("IntersectionObserver" in window) {
    const navObserver = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          sectionLinks.forEach((link) => {
            link.classList.toggle("is-active", link.getAttribute("href") === `#${entry.target.id}`);
          });
        });
      },
      { rootMargin: "-38% 0px -50% 0px", threshold: 0 }
    );
    sections.forEach((section) => navObserver.observe(section));
  }

  setSuite("overview");
  updateScrollEffects();
})();
