// All navigation, downloads and disclosure controls work without JavaScript.
// Finite entrance motion is a progressive enhancement; content is never hidden.
(() => {
  const preference = window.matchMedia('(prefers-reduced-motion: reduce)');
  const animations = new Set();
  if (!('IntersectionObserver' in window) || !Element.prototype.animate) return;
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      observer.unobserve(entry.target);
      if (preference.matches) continue;
      const animation = entry.target.animate(
        [{ translate: '0 18px' }, { translate: '0 0' }],
        { duration: 650, easing: 'cubic-bezier(.2,.7,.3,1)' }
      );
      animations.add(animation);
      animation.onfinish = () => animations.delete(animation);
    }
  }, { threshold: 0.1 });
  document.querySelectorAll('.hero-copy, .diagram, .feature, .steps, .download-panel, .identity, .project-card').forEach(el => observer.observe(el));
  preference.addEventListener('change', () => {
    if (preference.matches) {
      animations.forEach(animation => animation.cancel());
      animations.clear();
    }
  });
})();
