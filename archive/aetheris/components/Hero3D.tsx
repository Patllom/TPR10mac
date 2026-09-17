'use client';

import React, { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { useTheme } from './ThemeContext';

export default function Hero3D() {
  const containerRef = useRef<HTMLDivElement>(null);
  const { theme } = useTheme();
  const [webglSupported, setWebglSupported] = useState(true);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let renderer: THREE.WebGLRenderer | null = null;
    try {
      renderer = new THREE.WebGLRenderer({
        antialias: true,
        alpha: true,
        powerPreference: 'high-performance',
      });
    } catch (e) {
      console.warn('WebGL context creation error:', e);
      setWebglSupported(false);
      return;
    }

    if (!renderer || !renderer.domElement) {
      setWebglSupported(false);
      return;
    }

    // 1. SCENE & CAMERA
    const scene = new THREE.Scene();
    const width = container.clientWidth || window.innerWidth;
    const height = container.clientHeight || window.innerHeight;

    const camera = new THREE.PerspectiveCamera(42, width / height, 0.1, 1000);
    camera.position.set(0, 0, 8.2);

    renderer.setSize(width, height);
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.35;
    container.appendChild(renderer.domElement);

    // 2. LIGHTING RIG
    const ambientLight = new THREE.AmbientLight(0xffffff, 2.0);
    scene.add(ambientLight);

    const sunLight = new THREE.DirectionalLight(0xffffff, 3.5);
    sunLight.position.set(6, 8, 7);
    scene.add(sunLight);

    const cyanRimLight = new THREE.DirectionalLight(0x00f0ff, 2.8);
    cyanRimLight.position.set(-6, -3, -5);
    scene.add(cyanRimLight);

    const magentaRimLight = new THREE.PointLight(0xd946ef, 3.5, 30);
    magentaRimLight.position.set(4, -5, -4);
    scene.add(magentaRimLight);

    // 3. 3D GLOBE GROUP (Positioned on the Right for desktop viewport)
    const globeGroup = new THREE.Group();
    globeGroup.rotation.z = 0.18; // Earth's natural axial tilt
    globeGroup.position.x = window.innerWidth >= 1024 ? 1.7 : 0;
    globeGroup.position.y = window.innerWidth >= 1024 ? 0.05 : -0.3;
    scene.add(globeGroup);

    const GLOBE_RADIUS = 2.3;

    function latLngToVector3(lat: number, lng: number, radius: number): THREE.Vector3 {
      const phi = (90 - lat) * (Math.PI / 180);
      const theta = (lng + 180) * (Math.PI / 180);
      const x = -(radius * Math.sin(phi) * Math.cos(theta));
      const z = radius * Math.sin(phi) * Math.sin(theta);
      const y = radius * Math.cos(phi);
      return new THREE.Vector3(x, y, z);
    }

    // A. PROCEDURAL EARTH TEXTURE CANVAS
    const generateEarthTexture = (isDark: boolean) => {
      const canvas = document.createElement('canvas');
      canvas.width = 2048;
      canvas.height = 1024;
      const ctx = canvas.getContext('2d');
      if (!ctx) return new THREE.CanvasTexture(canvas);

      // Deep Ocean Gradient
      const oceanGrad = ctx.createLinearGradient(0, 0, 0, 1024);
      if (isDark) {
        oceanGrad.addColorStop(0, '#040b17');
        oceanGrad.addColorStop(0.5, '#071830');
        oceanGrad.addColorStop(1, '#02060f');
      } else {
        oceanGrad.addColorStop(0, '#0a2744');
        oceanGrad.addColorStop(0.5, '#0e3d68');
        oceanGrad.addColorStop(1, '#061a2e');
      }
      ctx.fillStyle = oceanGrad;
      ctx.fillRect(0, 0, 2048, 1024);

      // Lat/Lng Grid lines
      ctx.strokeStyle = isDark ? 'rgba(0, 240, 255, 0.12)' : 'rgba(217, 119, 6, 0.15)';
      ctx.lineWidth = 1.2;
      for (let x = 0; x < 2048; x += 128) {
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, 1024);
        ctx.stroke();
      }
      for (let y = 0; y < 1024; y += 128) {
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(2048, y);
        ctx.stroke();
      }

      // Draw Continents with Glowing Outlines
      ctx.fillStyle = isDark ? '#112233' : '#1b3752';
      ctx.strokeStyle = isDark ? '#00f0ff' : '#60a5fa';
      ctx.lineWidth = 2.5;

      const continents = [
        [[300, 200], [550, 180], [600, 280], [480, 450], [380, 400], [280, 320]],
        [[450, 480], [580, 520], [550, 780], [480, 850], [420, 620]],
        [[950, 200], [1150, 180], [1180, 320], [1020, 380], [920, 280]],
        [[950, 400], [1180, 420], [1150, 700], [1050, 800], [920, 550]],
        [[1180, 200], [1650, 180], [1700, 450], [1450, 520], [1250, 400]],
        [[1550, 650], [1750, 620], [1720, 800], [1580, 820]],
      ];

      continents.forEach((poly) => {
        ctx.beginPath();
        ctx.moveTo(poly[0][0], poly[0][1]);
        for (let i = 1; i < poly.length; i++) {
          ctx.lineTo(poly[i][0], poly[i][1]);
        }
        ctx.closePath();
        ctx.fill();
        ctx.stroke();
      });

      // City Lights / Fiber nodes
      for (let i = 0; i < 750; i++) {
        const poly = continents[i % continents.length];
        const cx = poly[0][0] + (Math.random() - 0.2) * 220;
        const cy = poly[0][1] + (Math.random() - 0.2) * 220;

        ctx.fillStyle = Math.random() > 0.35 ? '#00f0ff' : '#ff00aa';
        ctx.shadowColor = ctx.fillStyle;
        ctx.shadowBlur = 10;
        ctx.beginPath();
        ctx.arc(cx % 2048, cy % 1024, Math.random() * 2.8 + 1, 0, Math.PI * 2);
        ctx.fill();
      }

      const texture = new THREE.CanvasTexture(canvas);
      texture.wrapS = THREE.RepeatWrapping;
      texture.wrapT = THREE.ClampToEdgeWrapping;
      return texture;
    };

    let earthTexture = generateEarthTexture(theme === 'dark');

    // B. MAIN EARTH SPHERE
    const earthGeo = new THREE.SphereGeometry(GLOBE_RADIUS, 64, 64);
    const earthMat = new THREE.MeshStandardMaterial({
      map: earthTexture,
      roughness: 0.2,
      metalness: 0.35,
    });
    const earthMesh = new THREE.Mesh(earthGeo, earthMat);
    globeGroup.add(earthMesh);

    // C. ATMOSPHERE FRESNEL GLOW
    const atmosphereGeo = new THREE.SphereGeometry(GLOBE_RADIUS * 1.07, 48, 48);
    const atmosphereMat = new THREE.ShaderMaterial({
      vertexShader: `
        varying vec3 vNormal;
        void main() {
          vNormal = normalize(normalMatrix * normal);
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
      `,
      fragmentShader: `
        varying vec3 vNormal;
        uniform vec3 glowColor;
        void main() {
          float intensity = pow(0.68 - dot(vNormal, vec3(0, 0, 1.0)), 2.5);
          gl_FragColor = vec4(glowColor, 1.0) * intensity * 2.2;
        }
      `,
      uniforms: {
        glowColor: { value: new THREE.Color(0x00f0ff) },
      },
      blending: THREE.AdditiveBlending,
      side: THREE.BackSide,
      transparent: true,
    });
    const atmosphereMesh = new THREE.Mesh(atmosphereGeo, atmosphereMat);
    globeGroup.add(atmosphereMesh);

    // D. GLOBAL METROPOLITAN CITY NODES
    const cities = [
      { name: 'Bangkok', lat: 13.7563, lng: 100.5018, color: '#00f0ff' },
      { name: 'Tokyo', lat: 35.6762, lng: 139.6503, color: '#ff00aa' },
      { name: 'Singapore', lat: 1.3521, lng: 103.8198, color: '#00f0ff' },
      { name: 'Hong Kong', lat: 22.3193, lng: 114.1694, color: '#00f0ff' },
      { name: 'Seoul', lat: 37.5665, lng: 126.978, color: '#ff00aa' },
      { name: 'Dubai', lat: 25.2048, lng: 55.2708, color: '#00f0ff' },
      { name: 'London', lat: 51.5074, lng: -0.1278, color: '#ff00aa' },
      { name: 'Frankfurt', lat: 50.1109, lng: 8.6821, color: '#00f0ff' },
      { name: 'Paris', lat: 48.8566, lng: 2.3522, color: '#00f0ff' },
      { name: 'New York', lat: 40.7128, lng: -74.006, color: '#00f0ff' },
      { name: 'San Francisco', lat: 37.7749, lng: -122.4194, color: '#ff00aa' },
      { name: 'São Paulo', lat: -23.5505, lng: -46.6333, color: '#00f0ff' },
      { name: 'Sydney', lat: -33.8688, lng: 151.2093, color: '#ff00aa' },
    ];

    interface CityHub {
      name: string;
      position: THREE.Vector3;
      mesh: THREE.Mesh;
      flareRing: THREE.Mesh;
      colorHex: string;
    }

    const cityHubs: CityHub[] = [];
    const hubSphereGeo: THREE.BufferGeometry = new THREE.SphereGeometry(0.065, 16, 16);
    const flareRingGeo: THREE.BufferGeometry = new THREE.RingGeometry(0.08, 0.22, 32);

    cities.forEach((city) => {
      const pos = latLngToVector3(city.lat, city.lng, GLOBE_RADIUS * 1.015);

      const hubMat = new THREE.MeshBasicMaterial({
        color: new THREE.Color(city.color),
      });
      const hubMesh = new THREE.Mesh(hubSphereGeo, hubMat);
      hubMesh.position.copy(pos);
      globeGroup.add(hubMesh);

      const flareMat = new THREE.MeshBasicMaterial({
        color: new THREE.Color(city.color),
        transparent: true,
        opacity: 0.85,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
      });
      const flareRing = new THREE.Mesh(flareRingGeo, flareMat);
      flareRing.position.copy(pos.clone().multiplyScalar(1.008));
      flareRing.lookAt(pos.clone().multiplyScalar(2));
      globeGroup.add(flareRing);

      cityHubs.push({
        name: city.name,
        position: pos,
        mesh: hubMesh,
        flareRing,
        colorHex: city.color,
      });
    });

    // E. VIBRANT FIBER OPTIC ARCS (Cyan & Magenta)
    const fiberRoutes = [
      ['Bangkok', 'Tokyo', '#00f0ff'],
      ['Bangkok', 'Singapore', '#00f0ff'],
      ['Bangkok', 'Hong Kong', '#00f0ff'],
      ['Tokyo', 'San Francisco', '#ff00aa'],
      ['Tokyo', 'Seoul', '#00f0ff'],
      ['Tokyo', 'Hong Kong', '#00f0ff'],
      ['Singapore', 'Dubai', '#00f0ff'],
      ['Singapore', 'Sydney', '#ff00aa'],
      ['Dubai', 'Frankfurt', '#00f0ff'],
      ['Frankfurt', 'London', '#00f0ff'],
      ['Frankfurt', 'Paris', '#00f0ff'],
      ['London', 'New York', '#ff00aa'],
      ['London', 'Paris', '#00f0ff'],
      ['New York', 'San Francisco', '#00f0ff'],
      ['New York', 'São Paulo', '#ff00aa'],
      ['San Francisco', 'Sydney', '#00f0ff'],
      ['San Francisco', 'Tokyo', '#00f0ff'],
      ['Hong Kong', 'Singapore', '#ff00aa'],
      ['Paris', 'New York', '#00f0ff'],
      ['Dubai', 'London', '#00f0ff'],
    ];

    interface FiberArc {
      curve: THREE.CubicBezierCurve3;
      line: THREE.Line;
      color: string;
    }

    const fiberArcs: FiberArc[] = [];

    fiberRoutes.forEach(([fromName, toName, colorHex]) => {
      const fromHub = cityHubs.find((h) => h.name === fromName);
      const toHub = cityHubs.find((h) => h.name === toName);
      if (!fromHub || !toHub) return;

      const p1 = fromHub.position;
      const p2 = toHub.position;
      const distance = p1.distanceTo(p2);

      const elevation = GLOBE_RADIUS + Math.min(distance * 0.45, 1.8);
      const mid1 = p1.clone().lerp(p2, 0.33).normalize().multiplyScalar(elevation);
      const mid2 = p1.clone().lerp(p2, 0.66).normalize().multiplyScalar(elevation);

      const curve = new THREE.CubicBezierCurve3(p1, mid1, mid2, p2);
      const points = curve.getPoints(60);

      const arcGeo = new THREE.BufferGeometry().setFromPoints(points);
      const arcMat = new THREE.LineBasicMaterial({
        color: new THREE.Color(colorHex),
        transparent: true,
        opacity: 0.9,
        linewidth: 2,
        blending: THREE.AdditiveBlending,
      });

      const line = new THREE.Line(arcGeo, arcMat);
      globeGroup.add(line);

      fiberArcs.push({ curve, line, color: colorHex });
    });

    // F. DATA PACKETS
    const packetCount = 65;
    interface FiberPacket {
      arcIndex: number;
      progress: number;
      speed: number;
      mesh: THREE.Mesh;
    }
    const packets: FiberPacket[] = [];
    const packetSphereGeo: THREE.BufferGeometry = new THREE.SphereGeometry(0.05, 12, 12);

    for (let i = 0; i < packetCount; i++) {
      const arcIndex = Math.floor(Math.random() * fiberArcs.length);
      const arcColor = fiberArcs[arcIndex].color;

      const packetMat = new THREE.MeshBasicMaterial({
        color: new THREE.Color(arcColor === '#ff00aa' ? '#ffffff' : '#00f0ff'),
        blending: THREE.AdditiveBlending,
      });
      const mesh = new THREE.Mesh(packetSphereGeo, packetMat);
      globeGroup.add(mesh);

      packets.push({
        arcIndex,
        progress: Math.random(),
        speed: 0.007 + Math.random() * 0.015,
        mesh,
      });
    }

    // G. ORBITAL FIBER HALO RINGS
    const orbitalRings: THREE.Mesh[] = [];
    const ringConfigs = [
      { radius: 3.25, tiltX: Math.PI / 3.2, tiltZ: 0.15, color: 0x00f0ff },
      { radius: 3.65, tiltX: -Math.PI / 3.8, tiltZ: -0.25, color: 0xd946ef },
      { radius: 4.15, tiltX: Math.PI / 4.5, tiltZ: 0.35, color: 0x00f0ff },
    ];

    ringConfigs.forEach((cfg) => {
      const ringGeo = new THREE.TorusGeometry(cfg.radius, 0.014, 16, 160);
      const ringMat = new THREE.MeshBasicMaterial({
        color: cfg.color,
        transparent: true,
        opacity: 0.85,
        blending: THREE.AdditiveBlending,
      });
      const ringMesh = new THREE.Mesh(ringGeo, ringMat);
      ringMesh.rotation.x = cfg.tiltX;
      ringMesh.rotation.z = cfg.tiltZ;
      globeGroup.add(ringMesh);
      orbitalRings.push(ringMesh);
    });

    // H. RUNNING ORBITAL BEADS
    const orbitBeadCount = 48;
    interface OrbitBead {
      ringIndex: number;
      angle: number;
      speed: number;
      mesh: THREE.Mesh;
    }
    const orbitBeads: OrbitBead[] = [];
    const beadGeo: THREE.BufferGeometry = new THREE.SphereGeometry(0.045, 12, 12);

    for (let i = 0; i < orbitBeadCount; i++) {
      const ringIndex = i % 3;
      const beadMat = new THREE.MeshBasicMaterial({
        color: ringIndex === 1 ? 0xff00aa : 0x00f0ff,
        blending: THREE.AdditiveBlending,
      });
      const mesh = new THREE.Mesh(beadGeo, beadMat);
      globeGroup.add(mesh);
      orbitBeads.push({
        ringIndex,
        angle: Math.random() * Math.PI * 2,
        speed: 0.01 + Math.random() * 0.015,
        mesh,
      });
    }

    // I. AMBIENT NEBULA PARTICLES
    const starCount = 380;
    const starGeo = new THREE.BufferGeometry();
    const starPos = new Float32Array(starCount * 3);
    const starColors = new Float32Array(starCount * 3);

    for (let i = 0; i < starCount * 3; i += 3) {
      const r = 3.6 + Math.random() * 6.5;
      const theta = Math.random() * Math.PI * 2;
      const py = (Math.random() - 0.5) * 7.5;
      starPos[i] = Math.cos(theta) * r;
      starPos[i + 1] = py;
      starPos[i + 2] = Math.sin(theta) * r;

      const isMagenta = Math.random() > 0.6;
      starColors[i] = isMagenta ? 0.95 : 0.0;
      starColors[i + 1] = isMagenta ? 0.2 : 0.95;
      starColors[i + 2] = 1.0;
    }
    starGeo.setAttribute('position', new THREE.BufferAttribute(starPos, 3));
    starGeo.setAttribute('color', new THREE.BufferAttribute(starColors, 3));

    const starMat = new THREE.PointsMaterial({
      size: 0.048,
      vertexColors: true,
      transparent: true,
      opacity: 0.85,
      blending: THREE.AdditiveBlending,
    });
    const starPoints = new THREE.Points(starGeo, starMat);
    scene.add(starPoints);

    // 4. THEME SYNC
    const updateThemeMaterials = (isDark: boolean) => {
      earthTexture.dispose();
      earthTexture = generateEarthTexture(isDark);
      earthMat.map = earthTexture;
      earthMat.needsUpdate = true;

      if (isDark) {
        atmosphereMat.uniforms.glowColor.value.setHex(0x00f0ff);
        ambientLight.intensity = 1.8;
        ambientLight.color.setHex(0x0c1e36);
        sunLight.intensity = 3.0;
        sunLight.color.setHex(0x38bdf8);
      } else {
        atmosphereMat.uniforms.glowColor.value.setHex(0x38bdf8);
        ambientLight.intensity = 2.5;
        ambientLight.color.setHex(0xffffff);
        sunLight.intensity = 3.8;
        sunLight.color.setHex(0xfffbeb);
      }
    };

    updateThemeMaterials(theme === 'dark');

    // 5. MOUSE TRACKING
    const mouse = { x: 0, y: 0, targetX: 0, targetY: 0 };
    const handleMouseMove = (e: MouseEvent) => {
      mouse.targetX = (e.clientX / window.innerWidth) * 2 - 1;
      mouse.targetY = -(e.clientY / window.innerHeight) * 2 + 1;
    };
    const handleTouchMove = (e: TouchEvent) => {
      if (e.touches.length > 0) {
        mouse.targetX = (e.touches[0].clientX / window.innerWidth) * 2 - 1;
        mouse.targetY = -(e.touches[0].clientY / window.innerHeight) * 2 + 1;
      }
    };
    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('touchmove', handleTouchMove, { passive: true });

    const handleResize = () => {
      if (!container || !renderer) return;
      const newWidth = container.clientWidth || window.innerWidth;
      const newHeight = container.clientHeight || window.innerHeight;
      camera.aspect = newWidth / newHeight;
      camera.updateProjectionMatrix();
      renderer.setSize(newWidth, newHeight);
      renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

      globeGroup.position.x = newWidth >= 1024 ? 1.7 : 0;
      globeGroup.position.y = newWidth >= 1024 ? 0.05 : -0.3;
    };
    window.addEventListener('resize', handleResize);

    const handleCustomThemeChange = (e: Event) => {
      const customEvent = e as CustomEvent<{ theme: 'light' | 'dark' }>;
      updateThemeMaterials(customEvent.detail.theme === 'dark');
    };
    window.addEventListener('theme-change', handleCustomThemeChange);

    // 6. ANIMATION LOOP
    let animationFrameId: number;
    const clock = new THREE.Clock();

    const animate = () => {
      animationFrameId = requestAnimationFrame(animate);
      const t = clock.getElapsedTime();

      // Smooth mouse lerp
      mouse.x += (mouse.targetX - mouse.x) * 0.04;
      mouse.y += (mouse.targetY - mouse.y) * 0.04;

      // Globe rotation
      globeGroup.rotation.y = t * 0.15 + mouse.x * 0.45;
      globeGroup.rotation.x = 0.18 + Math.sin(t * 0.2) * 0.05 - mouse.y * 0.3;
      globeGroup.position.y = (window.innerWidth >= 1024 ? 0.05 : -0.3) + Math.sin(t * 0.8) * 0.07;

      // City flares
      cityHubs.forEach((hub, idx) => {
        const pulse = 1.0 + Math.sin(t * 3.5 + idx * 0.9) * 0.5;
        hub.flareRing.scale.set(pulse, pulse, pulse);
        const op = (1.0 - (pulse - 0.5) / 1.0) * 0.85;
        (hub.flareRing.material as THREE.MeshBasicMaterial).opacity = Math.max(0.15, op);
      });

      // Data packets
      packets.forEach((p) => {
        p.progress += p.speed;
        if (p.progress >= 1.0) {
          p.progress = 0;
          p.arcIndex = Math.floor(Math.random() * fiberArcs.length);
        }
        const arc = fiberArcs[p.arcIndex];
        const point = arc.curve.getPoint(p.progress);
        p.mesh.position.copy(point);
      });

      // Orbital Rings
      orbitalRings[0].rotation.z = t * 0.2;
      orbitalRings[1].rotation.y = -t * 0.18;
      orbitalRings[2].rotation.z = -t * 0.22;

      orbitBeads.forEach((bead) => {
        bead.angle += bead.speed;
        const cfg = ringConfigs[bead.ringIndex];
        const radius = cfg.radius;
        const bx = Math.cos(bead.angle) * radius;
        const bz = Math.sin(bead.angle) * radius;
        const localPos = new THREE.Vector3(bx, 0, bz);
        const euler = new THREE.Euler(cfg.tiltX, 0, cfg.tiltZ);
        localPos.applyEuler(euler);
        bead.mesh.position.copy(localPos);
      });

      // Stars drift
      starPoints.rotation.y = -t * 0.018 + mouse.x * 0.04;

      if (renderer) {
        renderer.render(scene, camera);
      }
    };

    animate();

    // 7. CLEANUP
    return () => {
      cancelAnimationFrame(animationFrameId);
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('touchmove', handleTouchMove);
      window.removeEventListener('resize', handleResize);
      window.removeEventListener('theme-change', handleCustomThemeChange);

      if (renderer && container.contains(renderer.domElement)) {
        container.removeChild(renderer.domElement);
      }

      earthGeo.dispose();
      earthMat.dispose();
      earthTexture.dispose();
      atmosphereGeo.dispose();
      atmosphereMat.dispose();
      hubSphereGeo.dispose();
      flareRingGeo.dispose();
      packetSphereGeo.dispose();
      beadGeo.dispose();
      starGeo.dispose();
      starMat.dispose();
      if (renderer) {
        renderer.dispose();
      }
    };
  }, [theme]);

  return (
    <div
      ref={containerRef}
      className="absolute inset-0 w-full h-full pointer-events-none overflow-hidden z-0"
      aria-hidden="true"
    >
      {!webglSupported && (
        <div className="w-full h-full bg-gradient-to-br from-slate-950 via-blue-950 to-slate-900 flex items-center justify-center opacity-80" />
      )}
    </div>
  );
}
