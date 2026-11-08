(() => {
  const body = document.body;
  const header = document.querySelector("[data-site-header]");
  const menuToggle = document.querySelector("[data-menu-toggle]");
  const mobileMenu = document.querySelector("[data-mobile-menu]");
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  if (!reducedMotion) document.documentElement.classList.add("n-snap");
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
  const audioPrompt = document.querySelector("[data-audio-prompt]");
  const audioAccept = document.querySelector("[data-audio-accept]");
  const audioDismiss = document.querySelector("[data-audio-dismiss]");
  const musicStorageKey = "freshstart:background-music";
  const musicVolume = 0.18;
  let musicPreference = null;
  let musicStarted = false;
  let musicFadeFrame = 0;
  let musicAutoplayBlocked = false;
  let musicStartPromise = null;

  try {
    musicPreference = window.localStorage.getItem(musicStorageKey);
  } catch {
    musicPreference = null;
  }
  let musicEnabled = musicPreference !== "off";

  const isMusicPlaying = () =>
    Boolean(
      musicEnabled
      && backgroundMusic
      && !backgroundMusic.paused
      && !backgroundMusic.ended
      && backgroundMusic.readyState >= 2
      && backgroundMusic.volume > 0.01
    );

  const updateAudioControls = () => {
    const playing = isMusicPlaying();
    audioToggles.forEach((button) => {
      button.setAttribute("aria-pressed", String(musicEnabled));
      button.setAttribute("aria-label", musicEnabled ? "关闭背景音乐" : "开启背景音乐");
      button.classList.toggle("is-waiting", musicEnabled && !playing);
      button.classList.toggle("is-playing", playing);
    });
  };

  const hideAudioPrompt = () => {
    if (audioPrompt) audioPrompt.hidden = true;
  };

  const showAudioPrompt = () => {
    if (!audioPrompt || !musicEnabled || musicPreference !== null || isMusicPlaying()) return;
    audioPrompt.hidden = false;
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
        backgroundMusic.volume = targetVolume;
        updateAudioControls();
        finish?.();
      }
    };
    musicFadeFrame = requestAnimationFrame(step);
  };

  const startMusic = async ({ fadeIn = false } = {}) => {
    if (!backgroundMusic || !musicEnabled) return;
    if (musicStartPromise) return musicStartPromise;

    musicStartPromise = (async () => {
      cancelAnimationFrame(musicFadeFrame);
      backgroundMusic.muted = false;
      backgroundMusic.volume = fadeIn ? Math.min(0.03, musicVolume) : musicVolume;

      if (!backgroundMusic.paused) {
        musicStarted = true;
        musicAutoplayBlocked = false;
        hideAudioPrompt();
        updateAudioControls();
        fadeMusic(musicVolume);
        return;
      }

      try {
        await backgroundMusic.play();
        if (backgroundMusic.paused) throw new Error("Audio was paused before playback started.");
        musicStarted = true;
        musicAutoplayBlocked = false;
        hideAudioPrompt();
        updateAudioControls();
        if (fadeIn) fadeMusic(musicVolume);
      } catch {
        musicStarted = false;
        musicAutoplayBlocked = true;
        backgroundMusic.volume = 0;
        updateAudioControls();
        if (document.documentElement.classList.contains("has-preloaded")) {
          window.setTimeout(showAudioPrompt, 120);
        }
      } finally {
        musicStartPromise = null;
      }
    })();

    return musicStartPromise;
  };

  ["play", "playing", "pause", "ended", "waiting", "canplay"].forEach((eventName) => {
    backgroundMusic?.addEventListener(eventName, updateAudioControls);
  });

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
      musicPreference = musicEnabled ? "on" : "off";
      window.localStorage.setItem(musicStorageKey, musicPreference);
    } catch {}
    if (musicEnabled) {
      updateAudioControls();
      startMusic();
    } else {
      hideAudioPrompt();
      stopMusic();
    }
  };

  audioToggles.forEach((button) => button.addEventListener("click", toggleMusic));
  audioAccept?.addEventListener("click", () => {
    musicEnabled = true;
    musicPreference = "on";
    try {
      window.localStorage.setItem(musicStorageKey, "on");
    } catch {}
    hideAudioPrompt();
    updateAudioControls();
    startMusic();
  });
  audioDismiss?.addEventListener("click", () => {
    musicEnabled = false;
    musicPreference = "off";
    try {
      window.localStorage.setItem(musicStorageKey, "off");
    } catch {}
    hideAudioPrompt();
    updateAudioControls();
  });
  updateAudioControls();

  const startMusicAfterGesture = (event) => {
    if (
      event.target instanceof Element
      && event.target.closest("[data-audio-toggle], [data-audio-prompt]")
    ) {
      return;
    }
    if (musicEnabled && !isMusicPlaying()) startMusic();
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
      startMusic({ fadeIn: true });
      musicObserver?.disconnect();
    }
  };
  const musicObserver = preloader
    ? new MutationObserver(startMusicWhenLetterAppears)
    : null;
  musicObserver?.observe(preloader, { attributes: true, attributeFilter: ["class"] });
  startMusicWhenLetterAppears();

  if (!preloader && document.documentElement.classList.contains("has-preloaded")) {
    window.setTimeout(() => startMusic({ fadeIn: true }), 180);
  }

  window.addEventListener("freshstart:loaded", () => {
    if (musicAutoplayBlocked && musicEnabled && !isMusicPlaying()) {
      window.setTimeout(showAudioPrompt, 420);
    }
  }, { once: true });

  document.addEventListener("visibilitychange", () => {
    if (!backgroundMusic || !musicEnabled) return;
    if (document.hidden) {
      if (!backgroundMusic.paused) backgroundMusic.pause();
      updateAudioControls();
    } else if (!isMusicPlaying()) {
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
      copy: "CPU、内存、主板、显卡与网络集中呈现，系统基线清晰可查。",
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

  const pageSections = [
    ...document.querySelectorAll(
      ".n-hero, .n-statement, .n-suite, .n-feature-wall, .n-process, .n-handoff, .n-download, .n-faq, .n-footer"
    )
  ];
  const suitePageIndex = pageSections.indexOf(suiteScene);
  let pageLockedUntil = 0;
  let wheelAmount = 0;
  let wheelResetTimer = 0;

  const getCurrentPageIndex = () => {
    const anchor = window.scrollY + window.innerHeight * 0.42;
    let current = 0;
    pageSections.forEach((section, index) => {
      if (section.offsetTop <= anchor) current = index;
    });
    return current;
  };

  const advancePage = (direction) => {
    const now = performance.now();
    if (now < pageLockedUntil) return;

    const currentPage = getCurrentPageIndex();
    if (currentPage === suitePageIndex && suiteTabs.length) {
      const activeIndex = Math.max(
        0,
        suiteTabs.findIndex((tab) => tab.dataset.suiteTab === activeSuiteKey)
      );
      const nextSuiteIndex = activeIndex + direction;
      if (nextSuiteIndex >= 0 && nextSuiteIndex < suiteTabs.length) {
        setSuite(suiteTabs[nextSuiteIndex].dataset.suiteTab);
        pageLockedUntil = now + 620;
        return;
      }
    }

    const targetIndex = clamp(currentPage + direction, 0, pageSections.length - 1);
    if (targetIndex === currentPage) return;

    if (targetIndex === suitePageIndex) {
      setSuite(direction > 0 ? "overview" : "automation");
    }

    pageLockedUntil = now + 900;
    pageSections[targetIndex].scrollIntoView({
      behavior: reducedMotion ? "auto" : "smooth",
      block: "start"
    });
  };

  const supportsPagedWheel =
    !reducedMotion
    && window.innerWidth > 1024
    && window.matchMedia("(hover: hover) and (pointer: fine)").matches;

  if (supportsPagedWheel) {
    window.addEventListener(
      "wheel",
      (event) => {
        if (event.ctrlKey || body.classList.contains("n-menu-open")) return;
        if (document.documentElement.classList.contains("is-preloading")) return;
        if (Math.abs(event.deltaX) > Math.abs(event.deltaY)) return;

        event.preventDefault();
        const multiplier =
          event.deltaMode === 1
            ? 16
            : event.deltaMode === 2
              ? window.innerHeight
              : 1;
        wheelAmount += event.deltaY * multiplier;
        clearTimeout(wheelResetTimer);
        wheelResetTimer = window.setTimeout(() => {
          wheelAmount = 0;
        }, 140);

        if (Math.abs(wheelAmount) < 24) return;
        const direction = wheelAmount > 0 ? 1 : -1;
        wheelAmount = 0;
        advancePage(direction);
      },
      { passive: false }
    );

    document.addEventListener("keydown", (event) => {
      if (event.repeat || event.ctrlKey || event.metaKey || event.altKey) return;
      if (event.target instanceof Element && event.target.closest("input, textarea, select")) {
        return;
      }
      if (["ArrowDown", "PageDown", " "].includes(event.key)) {
        event.preventDefault();
        advancePage(1);
      } else if (["ArrowUp", "PageUp"].includes(event.key)) {
        event.preventDefault();
        advancePage(-1);
      } else if (event.key === "Home") {
        event.preventDefault();
        pageSections[0]?.scrollIntoView({ behavior: "smooth", block: "start" });
      } else if (event.key === "End") {
        event.preventDefault();
        pageSections.at(-1)?.scrollIntoView({ behavior: "smooth", block: "start" });
      }
    });
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
