(() => {
  const preloader = document.querySelector("[data-preloader]");
  if (!preloader) return;

  const fill = preloader.querySelector("[data-preloader-progress]");
  const rail = preloader.querySelector("[data-preloader-rail]");
  const canvas = preloader.querySelector("[data-preloader-canvas]");
  const context = canvas?.getContext("2d");
  const compositionCanvas = document.createElement("canvas");
  const compositionContext = compositionCanvas.getContext("2d");
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const minimumDuration = reduceMotion ? 180 : 2050;
  const preloadImages = [
    "site-assets/home-light.webp",
    "site-assets/home-dark.webp",
    "site-assets/system-light.webp",
    "site-assets/software-light.webp",
    "site-assets/settings-light.webp",
    "site-assets/execution-dark.webp",
    "site-assets/environment-migration.webp"
  ];
  const preloadFiles = [
    "download.html",
    "site-assets/bgm.mp3",
    "site-assets/MiSans-Light.woff2",
    "site-assets/MiSans-Regular.woff2",
    "site-assets/MiSans-Medium.woff2",
    "site-assets/MiSans-Semibold.woff2"
  ];
  let startedAt = null;
  let documentReady = document.readyState === "complete";
  let resourcesReady = false;
  let resourcesLoaded = 0;
  const resourceTotal = preloadImages.length + preloadFiles.length;
  let finished = false;
  let fAppeared = false;

  document.documentElement.classList.add("is-preloading");
  window.addEventListener("load", () => {
    documentReady = true;
  }, { once: true });

  const preloadImage = (src) => new Promise((resolve) => {
    const image = new Image();
    image.decoding = "async";
    image.onload = () => {
      resourcesLoaded++;
      resolve();
    };
    image.onerror = () => {
      resourcesLoaded++;
      resolve();
    };
    image.src = src;
  });

  const preloadFile = (src) => fetch(src, { cache: "force-cache" })
    .catch(() => null)
    .finally(() => {
      resourcesLoaded++;
    });

  Promise.allSettled([
    ...preloadImages.map(preloadImage),
    ...preloadFiles.map(preloadFile)
  ]).then(() => {
    resourcesReady = true;
  });

  const setProgress = (value) => {
    const progress = Math.max(0, Math.min(100, Math.round(value)));
    fill.style.transform = `scaleX(${progress / 100})`;
    rail?.setAttribute("aria-valuenow", String(progress));
    return progress;
  };

  const finish = () => {
    if (finished) return;
    finished = true;
    setProgress(100);
    preloader.classList.add("is-folding");
    announceFAppeared();

    if (reduceMotion || !context) {
      window.setTimeout(cleanup, reduceMotion ? 200 : 2240);
      return;
    }

    window.setTimeout(startMaskTransition, 1180);
  };

  const announceFAppeared = () => {
    if (fAppeared) return;
    const notify = () => {
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          fAppeared = true;
          window.dispatchEvent(new CustomEvent("freshstart:f-appeared"));
        });
      });
    };
    window.setTimeout(notify, reduceMotion ? 0 : 640);
  };

  const resizeCanvas = () => {
    if (!canvas || !context || !compositionContext) return;
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    const width = Math.round(window.innerWidth * ratio);
    const height = Math.round(window.innerHeight * ratio);
    canvas.width = width;
    canvas.height = height;
    compositionCanvas.width = width;
    compositionCanvas.height = height;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    compositionContext.setTransform(ratio, 0, 0, ratio, 0, 0);
  };

  const drawLetter = (target, scale, rotation) => {
    target.save();
    target.translate(window.innerWidth / 2, window.innerHeight / 2);
    target.rotate(rotation);
    target.scale(scale, scale);
    target.beginPath();
    target.moveTo(-80, -98);
    target.lineTo(80, -98);
    target.lineTo(80, -64);
    target.lineTo(-46, -64);
    target.lineTo(-46, -16);
    target.lineTo(34, -16);
    target.lineTo(34, 18);
    target.lineTo(-46, 18);
    target.lineTo(-46, 98);
    target.lineTo(-80, 98);
    target.closePath();
    target.fill();
    target.restore();
  };

  const smoothstep = (from, to, value) => {
    const ratio = Math.max(0, Math.min(1, (value - from) / (to - from)));
    return ratio * ratio * (3 - 2 * ratio);
  };

  const cleanup = () => {
    if (!preloader.isConnected) return;
    preloader.remove();
    document.documentElement.classList.remove("is-preloading", "is-page-revealing");
    document.documentElement.classList.add("has-preloaded");
    window.dispatchEvent(new CustomEvent("freshstart:loaded"));
  };

  const startMaskTransition = () => {
    if (!context || !compositionContext) {
      cleanup();
      return;
    }

    resizeCanvas();
    preloader.classList.add("is-canvas");
    window.addEventListener("resize", resizeCanvas, { passive: true });

    const duration = 1080;
    const transitionStart = performance.now();

    const frame = (now) => {
      if (!preloader.isConnected) return;
      const progress = Math.min(1, (now - transitionStart) / duration);
      const maxScale = Math.max(window.innerWidth / 114, window.innerHeight / 34) * 1.26;
      const growth =
        progress * progress * 0.42
        + smoothstep(0.62, 0.88, progress) * 0.58;
      const scale = 1 + (maxScale - 1) * growth;
      const rotation = smoothstep(0.2, 0.95, progress) * 0.18;
      const letterAlpha = 1 - smoothstep(0.2, 1, progress);

      compositionContext.clearRect(0, 0, window.innerWidth, window.innerHeight);
      compositionContext.globalCompositeOperation = "source-over";
      compositionContext.globalAlpha = 1;
      compositionContext.fillStyle = "#030405";
      compositionContext.fillRect(0, 0, window.innerWidth, window.innerHeight);

      compositionContext.globalCompositeOperation = "destination-out";
      compositionContext.fillStyle = "#000";
      drawLetter(compositionContext, scale, rotation);

      compositionContext.globalCompositeOperation = "source-over";
      compositionContext.globalAlpha = letterAlpha;
      compositionContext.fillStyle = "#f7f8f4";
      drawLetter(compositionContext, scale, rotation);

      compositionContext.globalAlpha = 1;
      context.clearRect(0, 0, window.innerWidth, window.innerHeight);
      context.drawImage(
        compositionCanvas,
        0,
        0,
        compositionCanvas.width,
        compositionCanvas.height,
        0,
        0,
        window.innerWidth,
        window.innerHeight
      );

      if (progress > 0.78) preloader.classList.add("is-exiting");
      if (progress < 1) {
        requestAnimationFrame(frame);
        return;
      }

      window.removeEventListener("resize", resizeCanvas);
      cleanup();
    };

    requestAnimationFrame(frame);
  };

  const tick = (now) => {
    if (startedAt === null) {
      startedAt = now;
      requestAnimationFrame(tick);
      return;
    }
    const elapsed = now - startedAt;
    const timeProgress = Math.min(1, elapsed / minimumDuration);
    const eased = 1 - (1 - timeProgress) ** 3;
    const resourceProgress = (resourcesLoaded / resourceTotal) * 100;
    const cap = documentReady && resourcesReady ? 100 : 96;
    const progress = setProgress(Math.min(cap, eased * 100, resourceProgress));

    if (progress >= 100) {
      finish();
      return;
    }

    requestAnimationFrame(tick);
  };

  requestAnimationFrame(tick);
})();
