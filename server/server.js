// server.js — PartyKit game server for Webtry
// Authoritative physics server, 60Hz tick, 20Hz state broadcast

// ===== Constants =====
const FDT = 1 / 60;
const SYNC_INTERVAL = 3; // broadcast every 3 ticks = 20Hz
const MAX_PLAYERS = 8;

// ===== Plane Physics (simple arcade flight model) =====
const GRAVITY        = 600;    // px/s²
const THRUST         = 300;    // px/s² forward along plane direction
const LIFT           = 700;    // px/s² in plane's up direction (overcomes gravity)
const PITCH_UP_RATE  = 2.8;    // rad/s constant nose-up rotation while throttling
const NOSE_FALL_RATE = 1.5;    // rad/s max nose-fall rate without throttle
const MAX_SPEED      = 500;    // px/s
const DRAG           = 0.5;    // linear drag coefficient
const BULLET_SPEED   = 600;    // px/s
const CRASH_SPEED    = 200;    // px/s downward vy to crash on ground contact

// ===== World =====
const W = 1920, H = 1080;

// ===== Helpers =====
function deltaAngle(a, b) {
  let d = b - a;
  while (d > Math.PI) d -= Math.PI * 2;
  while (d < -Math.PI) d += Math.PI * 2;
  return d;
}
const GROUND_Y = H - 40;
const CEILING = 20;

// ===== Player =====
class Player {
  constructor(idx) {
    this.idx = idx;
    this.x = 200 + Math.random() * 200;
    this.y = GROUND_Y;
    this.vx = 0; this.vy = 0;
    this.angle = 0;     // radians, 0 = right
    this.flipped = false;
    this.throttle = false;
    this.alive = true;
    this.occupied = false;
  }
  reset() {
    this.x = 200 + Math.random() * 200;
    this.y = GROUND_Y;
    this.vx = 0; this.vy = 0;
    this.angle = 0;
    this.flipped = false;
    this.throttle = false;
    this.alive = true;
  }
  update(dt) {
    if (!this.occupied || !this.alive) return;

    const vertSign = this.flipped ? -1 : 1;
    const onGround = this.y >= GROUND_Y;

    // --- Angle (direct control, no angular velocity) ---
    if (this.throttle) {
      // Pitch up: decrease angle (nose up in y-down canvas)
      this.angle -= PITCH_UP_RATE * vertSign * dt;
    } else if (!onGround) {
      // Nose falls toward pointing down (PI/2 in y-down canvas)
      const target = Math.PI / 2; // straight down
      const diff = deltaAngle(this.angle, target);
      const step = NOSE_FALL_RATE * dt;
      if (Math.abs(diff) <= step) {
        this.angle = target;
      } else {
        this.angle += Math.sign(diff) * step;
      }
    }
    // Normalize angle to [-PI, PI]
    while (this.angle > Math.PI) this.angle -= Math.PI * 2;
    while (this.angle < -Math.PI) this.angle += Math.PI * 2;

    // --- Forces ---
    if (this.throttle) {
      // Thrust along plane forward
      this.vx += Math.cos(this.angle) * THRUST * dt;
      this.vy += Math.sin(this.angle) * THRUST * dt;

      // Lift in plane's up direction (only when upright)
      const upX = Math.sin(this.angle) * vertSign;
      const upY = -Math.cos(this.angle) * vertSign;
      const liftScale = Math.max(0, -upY); // 1 when level, 0 when inverted
      this.vx += upX * LIFT * liftScale * dt;
      this.vy += upY * LIFT * liftScale * dt;
    }

    // Gravity (only when airborne)
    if (!onGround) {
      this.vy += GRAVITY * dt;
    }

    // Linear drag
    const dragFactor = 1 / (1 + DRAG * dt);
    this.vx *= dragFactor;
    this.vy *= dragFactor;

    // Speed cap
    const spd = Math.sqrt(this.vx * this.vx + this.vy * this.vy);
    if (spd > MAX_SPEED) {
      this.vx *= MAX_SPEED / spd;
      this.vy *= MAX_SPEED / spd;
    }

    // --- Move ---
    this.x += this.vx * dt;
    this.y += this.vy * dt;

    // --- Ground ---
    if (this.y >= GROUND_Y) {
      this.y = GROUND_Y;
      if (this.vy > CRASH_SPEED) {
        this.alive = false; // crash
      } else {
        this.vy = 0;
        this.vx *= 0.95; // ground friction
      }
    }

    // --- Ceiling ---
    if (this.y < CEILING) {
      this.y = CEILING;
      if (this.vy < 0) this.vy = 0;
    }
  }
  flip() {
    this.flipped = !this.flipped;
  }
}

// ===== Projectile =====
class Bullet {
  constructor(x, y, angle, ownerIdx) {
    this.x = x; this.y = y;
    this.vx = Math.cos(angle) * BULLET_SPEED;
    this.vy = Math.sin(angle) * BULLET_SPEED;
    this.owner = ownerIdx;
    this.life = 120; // frames
  }
  update() {
    this.x += this.vx * FDT;
    this.y += this.vy * FDT;
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
      a: p.angle, fl: p.flipped,
      t: p.throttle, alive: p.alive, occ: p.occupied
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
