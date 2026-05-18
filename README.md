# NPC AI Systems

> A modular Unity (C#) AI framework built for a naval combat game — featuring three purpose-built AI architectures that scale from simple reactive ships to multi-layered crew intelligence and boss-tier creature behaviour.

---

## Overview

This repository contains three independent but composable NPC AI systems, each designed for a distinct challenge domain within a ship-combat / nautical world:

| Folder | System | Use Case |
|--------|--------|----------|
| `NPCsShip/` | Simple Ship AI | Lightweight, reactive vessel behaviour |
| `NPCnewAI/` | 3-Layer Hierarchical AI | Intelligent pilots & crew with strategic awareness |
| `CreaturesAI/` | Creature AI | Boss-tier sea monsters — Kraken, Sea Ogre, and more |

---

## System 1 — Simple Ship AI (`NPCsShip/`)

A straightforward, performance-friendly AI for generic NPC vessels. Designed to populate the world with believable ship behaviour without the overhead of a full decision hierarchy.

**Core behaviours:**
- Patrol routes along configurable waypoints
- Player/enemy detection via proximity and line-of-sight triggers
- Pursuit, engagement, and retreat states
- Basic cannon-fire and evasion logic
- Scalable for large numbers of concurrent ship instances

**Best suited for:** ambient fleet ships, minor hostiles, escort vessels, and other NPCs that need solid behaviour at low CPU cost.

---

## System 2 — 3-Layer Hierarchical Ship AI (`NPCnewAI/`)

The flagship AI architecture. A three-tier decision stack that gives each ship — and every crew member aboard — coordinated, context-aware intelligence.

```
┌─────────────────────────────────────────────┐
│           STRATEGIC LAYER                   │
│   Arena Awareness · Fleet Coordination ·    │
│   Threat Prioritisation · Zone Control      │
├─────────────────────────────────────────────┤
│           TACTICAL LAYER                    │
│   Goal Assignment · Role Coordination ·     │
│   Crew Tasking · Mid-battle Adaptation      │
├─────────────────────────────────────────────┤
│           EXECUTION LAYER                   │
│   Per-Unit Behaviour · Pilot Navigation ·   │
│   Crew Actions · Combat Mechanics           │
└─────────────────────────────────────────────┘
```

### Strategic Layer — Arena Awareness
The top-level brain holds a live read on the entire battle arena. It evaluates fleet positions, threat levels, control zones, and win conditions to issue high-level directives downward.

- Tracks all active combatants and their threat ratings
- Manages ship-level objectives: engage, disengage, flank, defend
- Coordinates multi-ship tactics (flanking, encirclement, focus-fire)
- Responds to major arena events (ship sinking, zone shift, reinforcements)

### Tactical Layer — Goal Coordination
The mid-tier layer translates strategic directives into concrete crew assignments. It reasons about which crew roles are needed, where, and when.

- Assigns roles dynamically: helmsman, gunners, repair crew, boarders
- Coordinates timing of broadside volleys, boarding actions, repairs
- Re-tasks crew in response to casualties, hull breaches, or strategic changes
- Manages shared resources (ammunition, morale, stamina)

### Execution Layer — Per-Unit Behaviour
Individual pilots and crew members each run their own execution-layer logic, consuming tasks from the tactical layer and acting with local autonomy.

- **Pilot AI:** Pathfinding, wind-aware navigation, collision avoidance, firing-line positioning
- **Crew AI:** Station-specific routines — loading cannons, repairing hull segments, fighting boarders
- State machines per unit with smooth blending between task states
- Local sensing for immediate threat response (fire, enemy boarding, falling rigging)

**Best suited for:** named captain ships, faction flagships, escort commanders, and any vessel that needs to feel genuinely intelligent.

---

## System 3 — Creature AI (`CreaturesAI/`)

Purpose-built AI for massive sea creatures that function as environmental hazards and boss encounters. These are not ships — they require entirely different locomotion, attack patterns, and escalation logic.

**Implemented creatures:**
- **Kraken** — Multi-limb independent tentacle control, grab/drag/smash attacks, phase-based rage escalation, area denial with ink clouds
- **Sea Ogre** — Aggressive melee attacker, charge patterns, swipe range, threat-lock targeting

**Shared creature AI features:**
- Phase-based combat: behaviour escalates as health drops through defined thresholds
- Signature attacks with wind-up telegraph animations tied to AI state transitions
- Dynamic target selection: prioritises damaged ships, isolated vessels, or the player
- Cooldown-managed ability sequencing to prevent ability spam and maintain readability
- Defeat and retreat logic for non-fatal encounters

---

## Architecture Notes

All three systems are written in **C#** for Unity and follow these principles:

- **Separation of concerns** — perception, decision, and execution are distinct layers, not monolithic scripts
- **Data-driven configuration** — key parameters (detection radii, attack ranges, phase thresholds) are exposed as serialised fields for rapid designer iteration
- **Composable states** — state machines are designed to be extended; adding a new state does not require modifying existing ones
- **Performance-conscious** — coroutine-based update loops and distance-gated processing keep frame cost manageable even with many concurrent NPCs

---

## Repository Structure

```
NPC-AI-Systems/
├── NPCsShip/          # Simple reactive ship AI
├── NPCnewAI/          # 3-layer hierarchical AI (pilots + crew)
└── CreaturesAI/       # Boss creature AI (Kraken, Sea Ogre, etc.)
```

---

## Author

**M. Ahsan Ijaz** — Senior / Principal Unity Game Developer  
10+ years shipping multiplayer, XR, and mobile titles across global studios.  
[GitHub](https://github.com/muhammadahsanijaz) · [LinkedIn](https://www.linkedin.com/in/muhammadahsanijaz/)
