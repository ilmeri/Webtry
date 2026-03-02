// server.js — PartyKit game server for Webtry
// Authoritative physics server, 60Hz tick, 20Hz state broadcast

// ===== Constants =====
const FDT = 1 / 60;
const SYNC_INTERVAL = 3; // broadcast every 3 ticks = 20Hz
const MAX_PLAYERS = 8;

// ===== Plane Physics (ported from Unity PlanePhysics.cs) =====
const GRAVITY = 9.81;
const FLY_POWER = 12;
const FORWARD_ACCEL = 1.5;
const PITCH_UP_TORQUE = 2.5;       // rad/s² when throttle on
const PITCH_DOWN_TORQUE_MAX = 2;    // rad/s² max nose-down torque
const DOWN_TORQUE_EXPONENT = 2;
const MIN_PITCH_SPEED = 3;
const MAX_ANGULAR_SPEED = 2.094;    // ~120 deg/s in rad/s
const MAX_SPEED = 12;
const LINEAR_DRAG = 0.35;
const ANGULAR_DRAG = 2;
const AERO_LIFT_COEFF = 0.15;

// ===== World =====
const W = 1920, H = 1080;

// ===== Helpers =====
function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
function deltaAngle(a, b) {
  let d = b - a;
  while (d > Math.PI) d -= Math.PI * 2;
  while (d < -Math.PI) d += Math.PI * 2;
  return d;
}
function inverseLerp(a, b, v) {
  if (Math.abs(b - a) < 1e-9) return 0;
  return clamp01((v - a) / (b - a));
}

// ===== Player =====
class Player {
  constructor(idx) {
    this.idx = idx;
    this.x = 200 + Math.random() * 200;
    this.y = H / 2 + (Math.random() - 0.5) * 200;
    this.vx = 2; this.vy = 0;
    this.angle = 0;     // radians, 0 = right
    this.angVel = 0;    // angular velocity in rad/s
    this.flipped = false;
    this.throttle = false;
    this.alive = true;
    this.occupied = false;
  }
  reset() {
    this.x = 200 + Math.random() * 200;
    this.y = H / 2 + (Math.random() - 0.5) * 200;
    this.vx = 2; this.vy = 0;
    this.angle = 0;
    this.angVel = 0;
    this.flipped = false;
    this.throttle = false;
    this.alive = true;
  }
  update(dt) {
    if (!this.occupied || !this.alive) return;

    const vertSign = this.flipped ? -1 : 1;

    // Plane's local axes in canvas y-down coordinates:
    //   forward  = (cos(angle), sin(angle))
    //   upDir    = plane's up * vertSign = (sin(angle)*vertSign, -cos(angle)*vertSign)
    const fwdX = Math.cos(this.angle);
    const fwdY = Math.sin(this.angle);
    const upX = Math.sin(this.angle) * vertSign;
    const upY = -Math.cos(this.angle) * vertSign;

    // Forward speed (component of velocity along forward direction)
    const forwardSpeed = this.vx * fwdX + this.vy * fwdY;

    // worldUp in y-down canvas = (0, -1) (screen up)
    const worldUpX = 0, worldUpY = -1;

    if (this.throttle) {
      // --- Lift force: upDir * flyPower * directionalLift ---
      const directionalLift = clamp01(upX * worldUpX + upY * worldUpY);
      this.vx += upX * FLY_POWER * directionalLift * dt;
      this.vy += upY * FLY_POWER * directionalLift * dt;

      // --- Forward thrust ---
      this.vx += fwdX * FORWARD_ACCEL * dt;
      this.vy += fwdY * FORWARD_ACCEL * dt;

      // --- Pitch-up torque (decrease angle = pitch up in y-down) ---
      if (forwardSpeed >= MIN_PITCH_SPEED && Math.abs(this.angVel) < MAX_ANGULAR_SPEED) {
        // Pitch-up in y-down means angle decreasing, so torque is negative * vertSign
        this.angVel += -PITCH_UP_TORQUE * vertSign * dt;
      }
    }

    // --- Aerodynamic lift (always active) ---
    // liftDir = plane's up * vertSign (same as upDir)
    const aeroLiftDot = clamp01(upX * worldUpX + upY * worldUpY);
    const aeroForce = AERO_LIFT_COEFF * forwardSpeed * forwardSpeed * aeroLiftDot;
    this.vx += upX * aeroForce * dt;
    this.vy += upY * aeroForce * dt;

    // --- Pitch-down torque when throttle OFF ---
    if (!this.throttle) {
      // Reference angle: straight up or straight down depending on flipped
      // In y-down: "up" for non-flipped is -PI/2, "down" for flipped is +PI/2
      const reference = this.flipped ? Math.PI / 2 : -Math.PI / 2;
      const angleFromUp = deltaAngle(reference, this.angle);
      const factor = Math.pow(inverseLerp(0, Math.PI / 2, Math.abs(angleFromUp)), DOWN_TORQUE_EXPONENT);
      // Torque pushes nose downward: toward positive angle if angleFromUp > 0, negative if < 0
      const torqueDir = angleFromUp > 0 ? 1 : -1;
      this.angVel += torqueDir * PITCH_DOWN_TORQUE_MAX * factor * dt;
    }

    // --- Gravity (downward = +y in canvas) ---
    this.vy += GRAVITY * dt;

    // --- Angular drag ---
    this.angVel *= 1 / (1 + ANGULAR_DRAG * dt);

    // --- Clamp angular velocity ---
    if (this.angVel > MAX_ANGULAR_SPEED) this.angVel = MAX_ANGULAR_SPEED;
    if (this.angVel < -MAX_ANGULAR_SPEED) this.angVel = -MAX_ANGULAR_SPEED;

    // --- Apply angular velocity ---
    this.angle += this.angVel * dt;
    // Normalize angle to [-PI, PI]
    while (this.angle > Math.PI) this.angle -= Math.PI * 2;
    while (this.angle < -Math.PI) this.angle += Math.PI * 2;

    // --- Linear drag ---
    this.vx *= 1 / (1 + LINEAR_DRAG * dt);
    this.vy *= 1 / (1 + LINEAR_DRAG * dt);

    // --- Speed cap ---
    const spd = Math.sqrt(this.vx * this.vx + this.vy * this.vy);
    if (spd > MAX_SPEED) {
      this.vx *= MAX_SPEED / spd;
      this.vy *= MAX_SPEED / spd;
    }

    // --- Move ---
    this.x += this.vx * dt;
    this.y += this.vy * dt;

    // --- Ground / ceiling ---
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
    this.flipped = !this.flipped;
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
      a: p.angle, av: p.angVel, fl: p.flipped,
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
