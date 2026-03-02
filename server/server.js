// server.js — PartyKit game server for Webtry
// Authoritative physics server, 60Hz tick, 20Hz state broadcast

// ===== Constants =====
const FDT = 1 / 60;
const SYNC_INTERVAL = 3; // broadcast every 3 ticks = 20Hz
const MAX_PLAYERS = 8;

// ===== Plane Physics (tunable) =====
let THROTTLE_ACCEL = 0.15;
let THROTTLE_ROT = 2.5;   // rad/s rotation while throttling
let GRAVITY = 0.12;
let MAX_SPEED = 8;
let DRAG = 0.995;

// ===== World =====
const W = 1920, H = 1080;

// ===== Player =====
class Player {
  constructor(idx) {
    this.idx = idx;
    this.x = 200 + Math.random() * 200;
    this.y = H / 2 + (Math.random() - 0.5) * 200;
    this.vx = 2; this.vy = 0;
    this.angle = 0; // radians, 0 = right
    this.throttle = false;
    this.alive = true;
    this.occupied = false;
  }
  reset() {
    this.x = 200 + Math.random() * 200;
    this.y = H / 2 + (Math.random() - 0.5) * 200;
    this.vx = 2; this.vy = 0;
    this.angle = 0;
    this.throttle = false;
    this.alive = true;
  }
  update(dt) {
    if (!this.occupied || !this.alive) return;

    if (this.throttle) {
      // Accelerate forward + rotate upward
      this.angle -= THROTTLE_ROT * dt;
      this.vx += Math.cos(this.angle) * THROTTLE_ACCEL;
      this.vy += Math.sin(this.angle) * THROTTLE_ACCEL;
    }

    // Gravity
    this.vy += GRAVITY;

    // Align angle toward velocity when not throttling
    if (!this.throttle) {
      const velAngle = Math.atan2(this.vy, this.vx);
      let diff = velAngle - this.angle;
      while (diff > Math.PI) diff -= Math.PI * 2;
      while (diff < -Math.PI) diff += Math.PI * 2;
      this.angle += diff * 0.08;
    }

    // Drag
    this.vx *= DRAG;
    this.vy *= DRAG;

    // Speed cap
    const spd = Math.sqrt(this.vx * this.vx + this.vy * this.vy);
    if (spd > MAX_SPEED) {
      this.vx *= MAX_SPEED / spd;
      this.vy *= MAX_SPEED / spd;
    }

    // Move
    this.x += this.vx;
    this.y += this.vy;

    // Ground / ceiling
    if (this.y > H - 40) {
      this.y = H - 40;
      this.alive = false; // crash
    }
    if (this.y < 20) {
      this.y = 20;
      if (this.vy < 0) this.vy = 0;
    }
  }
  flip() {
    this.angle += Math.PI;
    // Normalize
    while (this.angle > Math.PI) this.angle -= Math.PI * 2;
    while (this.angle < -Math.PI) this.angle += Math.PI * 2;
  }
}

// ===== Projectile =====
class Bullet {
  constructor(x, y, angle, ownerIdx) {
    this.x = x; this.y = y;
    this.vx = Math.cos(angle) * 12;
    this.vy = Math.sin(angle) * 12;
    this.owner = ownerIdx;
    this.life = 120; // frames
  }
  update() {
    this.x += this.vx;
    this.y += this.vy;
    this.life--;
  }
}

// ===== PartyKit Server =====
export default class Server {
  constructor(room) {
    this.room = room;
    this.players = [];
    this.bullets = [];
    this.slots = {};       // connId -> playerIdx
    this.names = {};       // playerIdx -> name
    this.nextIdx = 0;
    this.syncCounter = 0;
    this.loopInterval = null;
    this.gameState = 'WAITING'; // WAITING, PLAYING
  }

  startLoop() {
    if (this.loopInterval) return;
    this.loopInterval = setInterval(() => this.tick(), 1000 / 60);
  }

  stopLoop() {
    if (this.loopInterval) {
      clearInterval(this.loopInterval);
      this.loopInterval = null;
    }
  }

  tick() {
    if (this.gameState !== 'PLAYING') return;

    // Update players
    for (const p of this.players) p.update(FDT);

    // Update bullets
    for (const b of this.bullets) b.update();
    this.bullets = this.bullets.filter(b => b.life > 0 && b.x > -50 && b.x < W + 50 && b.y > -50 && b.y < H + 50);

    // TODO: bullet-player collision, terrain collision

    // Broadcast state at 20Hz
    this.syncCounter++;
    if (this.syncCounter >= SYNC_INTERVAL) {
      this.syncCounter = 0;
      this.broadcast({ type: 'state', data: this.packState() });
    }
  }

  packState() {
    const ps = this.players.map(p => ({
      i: p.idx, x: p.x, y: p.y, vx: p.vx, vy: p.vy,
      a: p.angle, t: p.throttle, alive: p.alive, occ: p.occupied
    }));
    const bs = this.bullets.map(b => ({
      x: b.x, y: b.y, vx: b.vx, vy: b.vy, o: b.owner
    }));
    return { players: ps, bullets: bs };
  }

  broadcast(msg) {
    const data = JSON.stringify(msg);
    for (const conn of this.room.getConnections()) {
      conn.send(data);
    }
  }

  send(conn, msg) {
    conn.send(JSON.stringify(msg));
  }

  onConnect(conn) {
    if (this.players.length >= MAX_PLAYERS) {
      this.send(conn, { type: 'full' });
      return;
    }

    const idx = this.nextIdx++;
    const player = new Player(idx);
    player.occupied = true;
    this.players.push(player);
    this.slots[conn.id] = idx;

    this.send(conn, { type: 'assign', idx, players: this.getPlayerList() });
    this.broadcast({ type: 'joined', idx, players: this.getPlayerList() });

    // Auto-start when first player joins
    if (this.gameState === 'WAITING') {
      this.gameState = 'PLAYING';
      this.startLoop();
      this.broadcast({ type: 'start' });
    }
  }

  onClose(conn) {
    const idx = this.slots[conn.id];
    if (idx === undefined) return;
    delete this.slots[conn.id];
    delete this.names[idx];

    const pi = this.players.findIndex(p => p.idx === idx);
    if (pi >= 0) this.players.splice(pi, 1);

    this.broadcast({ type: 'left', idx, players: this.getPlayerList() });

    // Stop if empty
    if (this.players.length === 0) {
      this.stopLoop();
      this.gameState = 'WAITING';
    }
  }

  getPlayerList() {
    return this.players.map(p => ({ idx: p.idx, name: this.names[p.idx] || '' }));
  }

  onMessage(message, sender) {
    let msg;
    try { msg = JSON.parse(/** @type {string} */ (message)); } catch { return; }

    const idx = this.slots[sender.id];
    if (idx === undefined) return;
    const player = this.players.find(p => p.idx === idx);

    switch (msg.type) {
      case 'input': {
        if (!player || !player.alive) return;
        if (msg.action === 'throttle') player.throttle = !!msg.active;
        else if (msg.action === 'flip') player.flip();
        else if (msg.action === 'shoot') {
          this.bullets.push(new Bullet(player.x, player.y, player.angle, idx));
        }
        break;
      }
      case 'name': {
        this.names[idx] = (msg.name || '').replace(/[^a-zA-Z0-9 _\-]/g, '').slice(0, 10);
        this.broadcast({ type: 'players', players: this.getPlayerList() });
        break;
      }
      case 'respawn': {
        if (player && !player.alive) player.reset();
        break;
      }
    }
  }
}
