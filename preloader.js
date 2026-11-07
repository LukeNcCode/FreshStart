(() => {
  const preloader = document.querySelector("[data-preloader]");
  if (!preloader) return;

  const fill = preloader.querySelector("[data-preloader-progress]");
  const rail = preloader.querySelector("[data-preloader-rail]");
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const minimumDuration = reduceMotion ? 180 : 1550;
  const startedAt = performance.now();
  let documentReady = document.readyState === "complete";
  let finished = false;

  document.documentElement.classList.add("is-preloading");
  window.addEventListener("load", () => {
    documentReady = true;
  }, { once: true });

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

    window.setTimeout(() => {
      preloader.classList.add("is-exiting");
      document.documentElement.classList.add("is-page-revealing");
    }, reduceMotion ? 20 : 560);

    window.setTimeout(() => {
      preloader.remove();
      document.documentElement.classList.remove("is-preloading", "is-page-revealing");
      document.documentElement.classList.add("has-preloaded");
      window.dispatchEvent(new CustomEvent("freshstart:loaded"));
    }, reduceMotion ? 200 : 1560);
  };

  const tick = (now) => {
    const elapsed = now - startedAt;
    const timeProgress = Math.min(1, elapsed / minimumDuration);
    const eased = 1 - (1 - timeProgress) ** 3;
    const cap = documentReady ? 100 : 96;
    const progress = setProgress(Math.min(cap, eased * 100));

    if (progress >= 100) {
      finish();
      return;
    }

    requestAnimationFrame(tick);
  };

  requestAnimationFrame(tick);
})();
