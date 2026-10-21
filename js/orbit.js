/* Hero orbit: the art tilts toward the pointer, its layers shift by depth, the
   sphere's highlight follows like a light source, and a faint particle field
   drifts around the core and parts around the cursor. Decorative only; it is
   skipped entirely when the visitor prefers reduced motion. */
(function () {
  var art = document.querySelector('.hero-art');
  var stage = art && art.querySelector('.orbit-stage');
  if (!stage) return;
  var svg = stage.querySelector('svg');
  var canvas = stage.querySelector('.orbit-field');
  var ctx = canvas.getContext && canvas.getContext('2d');

  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    if (svg.pauseAnimations) svg.pauseAnimations();
    return;
  }

  var layers = [].map.call(svg.querySelectorAll('[data-depth]'), function (el) {
    return { el: el, depth: Number(el.getAttribute('data-depth')) };
  });
  var chips = [].slice.call(art.querySelectorAll('.orbit-chip'));
  var light = document.getElementById('sphere-light');
  var canHover = window.matchMedia('(hover: hover)').matches;

  // Pointer offset from the stage center, -1..1 on each axis. `cur` eases toward `aim`.
  var aim = { x: 0, y: 0 }, cur = { x: 0, y: 0 };
  // Pointer position in canvas pixels, for the particle field; null when outside.
  var pointer = null;

  if (canHover) {
    var hero = document.querySelector('.home-hero') || art;
    hero.addEventListener('pointermove', function (event) {
      var rect = stage.getBoundingClientRect();
      var half = rect.width / 2;
      aim.x = Math.max(-1, Math.min(1, (event.clientX - rect.left - half) / half));
      aim.y = Math.max(-1, Math.min(1, (event.clientY - rect.top - rect.height / 2) / half));
      var px = event.clientX - rect.left, py = event.clientY - rect.top;
      pointer = (px > -40 && py > -40 && px < rect.width + 40 && py < rect.height + 40) ? { x: px * dpr, y: py * dpr } : null;
    });
    hero.addEventListener('pointerleave', function () { aim.x = 0; aim.y = 0; pointer = null; });
  }

  // ── Particle field ──────────────────────────────────
  var dpr = 1, w = 0, h = 0, particles = [];
  function resize() {
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    w = canvas.width = Math.round(stage.clientWidth * dpr);
    h = canvas.height = Math.round(stage.clientHeight * dpr);
  }
  function seed() {
    particles = [];
    for (var i = 0; i < 90; i++) {
      var r = 0.24 + Math.pow(Math.random(), 0.7) * 0.25; // fraction of width, clustered toward the core
      particles.push({
        r: r,
        a: Math.random() * Math.PI * 2,
        speed: (0.00045 + Math.random() * 0.00025) / r, // inner particles orbit faster
        size: 0.5 + Math.random() * 1.1,
        alpha: 0.25 + Math.random() * 0.55,
        phase: Math.random() * Math.PI * 2,
        depth: 6 + Math.random() * 16,
        ox: 0, oy: 0 // displacement from the cursor, springs back to 0
      });
    }
  }

  var TILT = -0.42; // the field is a flattened disk, tilted like the brighter orbit ring
  var cosT = Math.cos(TILT), sinT = Math.sin(TILT);
  function drawField(t) {
    ctx.clearRect(0, 0, w, h);
    var cx = w / 2, cy = h / 2, push = 80 * dpr;
    ctx.fillStyle = '#d4c8ff';
    for (var i = 0; i < particles.length; i++) {
      var p = particles[i];
      p.a += p.speed;
      var ex = Math.cos(p.a) * p.r * w, ey = Math.sin(p.a) * p.r * w * 0.38;
      var x = cx + ex * cosT - ey * sinT + cur.x * p.depth * dpr;
      var y = cy + ex * sinT + ey * cosT + cur.y * p.depth * dpr;
      if (pointer) {
        var dx = x + p.ox - pointer.x, dy = y + p.oy - pointer.y, d = Math.sqrt(dx * dx + dy * dy);
        if (d < push && d > 0.01) {
          var f = (1 - d / push) * 2.2 * dpr;
          p.ox += (dx / d) * f; p.oy += (dy / d) * f;
        }
      }
      p.ox *= 0.93; p.oy *= 0.93;
      ctx.globalAlpha = p.alpha * (0.65 + 0.35 * Math.sin(t * 0.0016 + p.phase));
      ctx.beginPath();
      ctx.arc(x + p.ox, y + p.oy, p.size * dpr, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  // ── Frame loop (runs only while the hero is on screen) ──
  var running = false;
  function frame(t) {
    if (!running) return;
    cur.x += (aim.x - cur.x) * 0.07;
    cur.y += (aim.y - cur.y) * 0.07;
    stage.style.transform = 'perspective(1100px) rotateX(' + (-cur.y * 5).toFixed(2) + 'deg) rotateY(' + (cur.x * 7).toFixed(2) + 'deg)';
    for (var i = 0; i < layers.length; i++) {
      var d = layers[i].depth;
      layers[i].el.setAttribute('transform', 'translate(' + (cur.x * d).toFixed(2) + ' ' + (cur.y * d).toFixed(2) + ')');
    }
    for (var j = 0; j < chips.length; j++) {
      chips[j].style.translate = (cur.x * 22).toFixed(1) + 'px ' + (cur.y * 22).toFixed(1) + 'px';
    }
    if (light) {
      light.setAttribute('cx', (0.5 + cur.x * 0.3).toFixed(3));
      light.setAttribute('cy', (0.5 + cur.y * 0.3).toFixed(3));
    }
    if (ctx) drawField(t);
    requestAnimationFrame(frame);
  }
  function setRunning(on) {
    if (on === running) return;
    running = on;
    if (svg.pauseAnimations) on ? svg.unpauseAnimations() : svg.pauseAnimations();
    if (on) requestAnimationFrame(frame);
  }

  if (ctx) {
    resize(); seed();
    if (window.ResizeObserver) new ResizeObserver(resize).observe(stage);
    else window.addEventListener('resize', resize);
  }
  if (window.IntersectionObserver) {
    new IntersectionObserver(function (entries) { setRunning(entries[0].isIntersecting); }).observe(stage);
  } else {
    setRunning(true);
  }
})();
