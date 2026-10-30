export type SceneWeather = 'clear' | 'cloudy' | 'rain' | 'snow';
export type WeatherChoice = 'auto' | SceneWeather;
type Point = { x: number; y: number };

/** Own all SVG mutations and animation resources for one mounted login scene. */
export function createPelicanMotion(svg: SVGSVGElement, initialWeather: SceneWeather, initialPaused: boolean) {
  const document = svg.ownerDocument;
  const $ = (id: string): SVGElement => {
    const node = svg.querySelector<SVGElement>('#pelican-' + id);
    if (!node) throw new Error(`Missing pelican scene element: ${id}`);
    return node;
  };
  let paused = initialPaused, disposed = false;
  let elapsed = 0, lastTime = 0, animationId = 0;
  let particles: { node: SVGElement; x: number; y: number; seed: number }[] = [];
  let particleKind: SceneWeather | '' = '';
  // Foot and pedal share a transform; road travel equals wheel angle * radius.
  const geometry = { crank: { x: 398, y: 627 }, radius: 43, upperLeg: 108, lowerLeg: 112, wheelRadius: 111, gearRatio: 1.12 };
  const motionNodes = Object.fromEntries(['birdBody', 'pelicanHead', 'pelicanEye', 'tailFeathers', 'restingWing', 'steeringWing', 'wingDetail', 'scarfTail', 'frontSpokes', 'rearSpokes', 'chainringDetail', 'chainLinks', 'roadLines', 'roadDetails', 'cloudOne', 'cloudTwo', 'wind'].map((id) => [id, $(id)]));
  const legNodes = ['front', 'back'].map((side) => ({
    side, leg: $(side + 'Leg'), foot: $(side + 'Foot'), pedal: $(side + 'Pedal'), crank: $(side + 'Crank')
  }));
  const spokeLines = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  spokeLines.setAttribute('d', Array.from({ length: 20 }, (_, index) => {
    const rimAngle = index * Math.PI / 10;
    const hubAngle = rimAngle + (index % 2 ? -.85 : .85);
    return `M${Math.cos(hubAngle) * 8} ${Math.sin(hubAngle) * 8}L${Math.cos(rimAngle) * 95} ${Math.sin(rimAngle) * 95}`;
  }).join(''));
  $('spokePattern').append(spokeLines);
  function solveKnee(hip: Point, ankle: Point) {
    const dx = ankle.x - hip.x;
    const dy = ankle.y - hip.y;
    const distance = Math.hypot(dx, dy);
    const along = (geometry.upperLeg ** 2 - geometry.lowerLeg ** 2 + distance ** 2) / (2 * distance);
    const height = Math.sqrt(Math.max(0, geometry.upperLeg ** 2 - along ** 2));
    return { x: hip.x + along * dx / distance - height * dy / distance, y: hip.y + along * dy / distance + height * dx / distance };
  }
  const coordinate = (value: number) => value.toFixed(3);
  const pointString = (point: Point) => `${coordinate(point.x)} ${coordinate(point.y)}`;
  function motionAt(seconds: number) {
    const base = seconds * 2.75;
    const phase = .7 + base + .045 * Math.sin(base * 2);
    const wheelAngle = (phase - .7) * geometry.gearRatio;
    return { phase, wheelAngle, distance: wheelAngle * geometry.wheelRadius };
  }
  function drawBicycle(seconds: number, motion: ReturnType<typeof motionAt>) {
    const { phase, wheelAngle } = motion;
    const bob = 6 + Math.sin(phase * 2 - .3) * 2.1 + Math.sin(phase - .4) * .6;
    const sway = Math.sin(phase) * 1.4;
    const tilt = Math.sin(phase - .35) * .95;
    const radians = tilt * Math.PI / 180;
    const cosTilt = Math.cos(radians);
    const sinTilt = Math.sin(radians);
    const bodyPoint = (x: number, y: number) => ({ x: 365 + sway + (x - 365) * cosTilt - (y - 457) * sinTilt, y: 457 + bob + (x - 365) * sinTilt + (y - 457) * cosTilt });
    const n = motionNodes;
    n.birdBody.setAttribute('transform', `translate(${coordinate(sway)} ${coordinate(bob)}) rotate(${coordinate(tilt)} 365 457)`);
    n.pelicanHead.setAttribute('transform', `rotate(${coordinate(-tilt * .65 + Math.sin(phase - .85) * .6)} 418 390)`);
    n.tailFeathers.setAttribute('transform', `rotate(${coordinate(Math.sin(phase - .7) * 1.8)} 291 411)`);
    n.restingWing.setAttribute('transform', `rotate(${coordinate(Math.sin(phase - 1) * .85)} 341 380)`);
    const blinkTime = ((seconds + 1.8) % 6.1);
    const eyeOpen = blinkTime < .18 ? 1 - .94 * Math.sin(blinkTime / .18 * Math.PI) ** 2 : 1;
    n.pelicanEye.setAttribute('transform', `translate(0 280) scale(1 ${coordinate(eyeOpen)}) translate(0 -280)`);
    const shoulderTop = bodyPoint(420, 371);
    const shoulderBottom = bodyPoint(408, 393);
    const upperFeather = bodyPoint(446, 380);
    const lowerFeather = bodyPoint(431, 414);
    const elbow = Math.sin(phase - .5) * 1.6;
    n.steeringWing.setAttribute('d', `M${pointString(shoulderTop)} C${pointString(upperFeather)} 479 ${411 + elbow} 535 437 Q545 440 539 447 C511 453 480 ${435 + elbow} 463 ${423 + elbow} Q${pointString(lowerFeather)} ${pointString(shoulderBottom)}Z`);
    n.wingDetail.setAttribute('d', `M${pointString(bodyPoint(433, 391))} Q479 ${434 + elbow} 532 442 M512 439l19 6`);
    const flutter = Math.sin(phase * 1.5 - .8) * 2.4;
    n.scarfTail.setAttribute('d', `M421 341C403 ${343 + flutter} 393 ${348 - flutter} 371 ${340 + flutter}L385 ${356 + flutter}Q403 ${355 - flutter} 420 349Z`);
    for (const nodes of legNodes) {
      const angle = phase + (nodes.side === 'back' ? Math.PI : 0);
      const pedal = { x: geometry.crank.x + Math.cos(angle) * geometry.radius, y: geometry.crank.y + Math.sin(angle) * geometry.radius };
      const footDegrees = -3 + 7 * Math.sin(angle - .45);
      const footRadians = footDegrees * Math.PI / 180;
      const ankle = { x: pedal.x - 7 * Math.cos(footRadians) + 19 * Math.sin(footRadians), y: pedal.y - 7 * Math.sin(footRadians) - 19 * Math.cos(footRadians) };
      const hip = bodyPoint(nodes.side === 'back' ? 360 : 369, 457);
      const knee = solveKnee(hip, ankle);
      const kneeEntry = { x: knee.x + (hip.x - knee.x) * .1, y: knee.y + (hip.y - knee.y) * .1 };
      const kneeExit = { x: knee.x + (ankle.x - knee.x) * .1, y: knee.y + (ankle.y - knee.y) * .1 };
      const transform = `translate(${pointString(pedal)}) rotate(${coordinate(footDegrees)})`;
      nodes.foot.setAttribute('transform', transform);
      nodes.pedal.setAttribute('transform', transform);
      nodes.leg.setAttribute('d', `M${pointString(hip)} L${pointString(kneeEntry)} Q${pointString(knee)} ${pointString(kneeExit)} L${pointString(ankle)}`);
      nodes.crank.setAttribute('d', `M398 627 L${pointString(pedal)}`);
    }
    const wheelTransform = `rotate(${coordinate(wheelAngle * 180 / Math.PI)})`;
    n.frontSpokes.setAttribute('transform', wheelTransform);
    n.rearSpokes.setAttribute('transform', wheelTransform);
    n.chainringDetail.setAttribute('transform', `rotate(${coordinate(phase * 180 / Math.PI)} 398 627)`);
    n.chainLinks.setAttribute('stroke-dashoffset', coordinate(-phase * 20));
  }
  function buildParticles(weather: SceneWeather) {
    const kind = weather === 'rain' || weather === 'snow' ? weather : '';
    if (particleKind === kind) return;
    particleKind = kind;
    $('weatherEffects').replaceChildren();
    particles = [];
    if (!kind) return;
    const count = kind === 'rain' ? 82 : 56;
    for (let i = 0; i < count; i++) {
      const node = document.createElementNS('http://www.w3.org/2000/svg', kind === 'rain' ? 'path' : 'circle');
      const seed = ((i * 7919 + 104729) % 65521) / 65521;
      if (kind === 'rain') {
        node.setAttribute('d', `M0 0l-5 ${11 + seed * 8}`);
        node.setAttribute('stroke', 'var(--scene-muted)');
        node.setAttribute('stroke-width', String(1 + seed * .6));
        node.setAttribute('stroke-linecap', 'round');
        node.setAttribute('opacity', String(.18 + seed * .22));
      } else {
        node.setAttribute('r', String(1.3 + seed * 2));
        node.setAttribute('fill', '#f5fafb');
        node.setAttribute('opacity', String(.4 + seed * .5));
      }
      $('weatherEffects').append(node);
      particles.push({ node, x: (i * 173.3) % 940 - 50, y: (i * 283.7) % 1000, seed });
    }
  }
  const mod = (value: number, base: number) => ((value % base) + base) % base;
  function drawFrame(seconds: number) {
    const motion = motionAt(seconds);
    drawBicycle(seconds, motion);
    motionNodes.roadLines.setAttribute('transform', `translate(${coordinate(-mod(motion.distance, 208))} 0)`);
    motionNodes.roadDetails.setAttribute('transform', `translate(${coordinate(-mod(motion.distance, 180))} 0)`);
    motionNodes.cloudOne.setAttribute('transform', `translate(${Math.sin(seconds * .07) * 28} 0)`);
    motionNodes.cloudTwo.setAttribute('transform', `translate(${Math.sin(seconds * .05 + 1) * 18} 0)`);
    motionNodes.wind.setAttribute('transform', `translate(${-8 + Math.sin(seconds * .9) * 5} 0)`);
    for (const particle of particles) {
      const isRain = particleKind === 'rain';
      const speed = isRain ? 400 + particle.seed * 150 : 34 + particle.seed * 30;
      const y = mod(particle.y + seconds * speed, 1030) - 80;
      const x = isRain ? mod(particle.x - seconds * 125, 940) - 30 : particle.x + Math.sin(seconds * .7 + particle.seed * 10) * 17;
      particle.node.setAttribute('transform', `translate(${coordinate(x)} ${coordinate(y)})`);
    }
  }
  function tick(now: number) {
    animationId = 0;
    if (disposed || paused || document.hidden) { lastTime = 0; return; }
    if (lastTime) elapsed += Math.min((now - lastTime) / 1000, .05);
    lastTime = now;
    drawFrame(elapsed);
    animationId = requestAnimationFrame(tick);
  }
  function syncPlayback() {
    if (animationId) cancelAnimationFrame(animationId);
    animationId = 0;
    lastTime = 0;
    if (!disposed && !paused && !document.hidden) animationId = requestAnimationFrame(tick);
  }
  document.addEventListener('visibilitychange', syncPlayback);
  buildParticles(initialWeather);
  drawFrame(0);
  syncPlayback();
  return {
    setWeather(value: SceneWeather) { if (!disposed) { buildParticles(value); drawFrame(elapsed); } },
    setPaused(value: boolean) { if (paused !== value) { paused = value; syncPlayback(); } },
    dispose() {
      disposed = true;
      syncPlayback();
      document.removeEventListener('visibilitychange', syncPlayback);
      particles = [];
    },
  };
}
