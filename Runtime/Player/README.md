# Nox.XR.Player

Open-source, AutoHand-free player body. This is the modern replacement of AutoHand's
`AutoHandPlayer` (not of `XRController`: the player never reads input, a controller drives it).

Several `Player` instances can coexist in a scene (local player, debug/ghost player, NPC bodies):
the component has no singleton and every instance owns its own part registry. The active ones are
exposed read-only:

```csharp
IReadOnlyList<Player> players = Player.Instances;
int count = Player.InstanceCount;
```

The implementation is split by responsibility:

```
Runtime/Player/
├── Core/
│   ├── Player.cs              registry, lifecycle, part discovery
│   ├── Player.Movement.cs     input, rigidbody, body/head sync, turning
│   ├── Player.Grounding.cs    feet probing, capsule fallback, platforms, jump
│   ├── Player.Interaction.cs  grab / climb / push bridges
│   └── Player.Teleport.cs     teleport, rotation, hand relocation
├── Parts/
│   ├── Part.cs
│   ├── Head.cs
│   ├── Hand.cs
│   ├── Foot.cs
│   └── XRFollow.cs            per-part loader/binding driver
├── Interactions/
│   └── Interactables.cs
├── Connectors/
│   └── PlayerXRDriver.cs      move / turn / jump bindings
└── Prefabs/
    └── Player.prefab          ready-to-use multi-component player
```

```
Player (Rigidbody + CapsuleCollider)
├── EntityRig.Head       → Head   (camera pose, view height, head collision)
├── EntityRig.LeftHand   → Hand   (physics follow, grab / climb / push)
├── EntityRig.RightHand  → Hand
├── EntityRig.LeftFoot   → Foot   (ground probe, used by grounding)
└── EntityRig.RightFoot  → Foot
```

The prefab is instantiated as `Nox Player` in `Assets/OpenME.unity`.

## Parts

`Part` is the abstract base. Every part advertises the `EntityRig` slot it occupies, and
`Player` keeps a `Dictionary<EntityRig, Part>`:

- `Player.Head`, `LeftHand`, `RightHand`, `LeftFoot`, `RightFoot` resolve through the registry.
- `GetPart(EntityRig)` / `TryGetPart<T>(EntityRig, out T)` are the generic accessors.
- Nothing is discovered by scanning the hierarchy. `serializedParts` lists the parts the prefab
  wires explicitly (the head), and `PlayerPartBinder` calls `RegisterPart` for every hand, foot and
  tracker it creates. A part exists in the registry because somebody registered it, not because it
  happens to be a child of the player.

This removes AutoHand's hardcoded `handLeft` / `handRight` fields: adding a new limb only requires
a new `Part` subclass with its own rig slot.

### Enabling / disabling parts

Every part has a `partEnabled` toggle in the inspector (`Part.PartEnabled`). A disabled part stays
registered, but the player skips its update/fixed-update callbacks, ignores it for grounding, and
unsubscribes its hand interaction events.

```csharp
player.SetPartEnabled(EntityRig.LeftHand, false); // hide/ignore the left hand
player.SetPartEnabled(EntityRig.RightFoot, true);
bool enabled = player.IsPartEnabled(EntityRig.Head);
```

## Setting part values from XR loaders and bindings

Parts expose direct setters for every value carried by a tracked pose:

```csharp
part.SetPosition(position);
part.SetRotation(rotation);
part.SetVelocity(velocity);              // Rigidbody.linearVelocity on hands
part.SetAngularVelocity(angularVelocity);
part.SetPose(position, rotation);
part.Apply(transformObject);             // position + rotation + velocity + angular
```

`Player` mirrors them by rig, including the network wire format (`EntityRig` index):

```csharp
player.SetPart(EntityRig.LeftHand, position, rotation, velocity, angularVelocity);
player.SetPart(EntityRig.RightFoot.ToIndex(), transformObject);
```

### `XRFollow`, next to each part

`XRFollow` is a small component placed on the same GameObject as a part. It asks the loader layer
for the pose of the part's `EntityRig` and writes the values every frame.

`Node` is a **plain static facade** in `Runtime/Loaders/Node.cs` (not a MonoBehaviour). The actual
node poses come from the loader packages through `INodeProvider`:

- `nox.xr.openxr` → `OpenXRNodeProvider` (HMD, controllers);
- `nox.xr.openvr` → `OpenVRNodeProvider` (HMD, controllers);
- any other rig is resolved from `FullBodyCalibration` (tracker → `EntityRig` + stored offsets);
- `EntityRig.None` reads a free tracker (unclaimed by calibration), by serial or index.

`XRFollow` only handles the part side:

- `EntityRig` non-`None` → `Node.TryGetPose(rig, out pose)`;
- `EntityRig.None` → `Node.TryGetFreeTracker(serial/index, out pose)`;
- when the source disappears (`isTracked = false`, controller disconnected, tracker gone), the part is
  disabled, and re-enabled when the source comes back. A part disabled manually is left untouched.

`PlayerXRDriver` only keeps the input side: movement/turn/jump from `Keybindings`.

### Built-in parts

| Part | Rig | Responsibility |
|---|---|---|
| `Head` | `Head` | Camera pose, eye height, head collision sphere |
| `Hand` | `LeftHand` / `RightHand` | Rigidbody follow, grabbing, climbing and pushing hooks |
| `Foot` | `LeftFoot` / `RightFoot` | Ground probing used before the capsule fallback |

## Interfaces replacing AutoHand components

| AutoHand | Nox |
|---|---|
| `Grabbable` | `IGrabbable` |
| `Climbable` | `IClimbable` |
| `Pushable` | `IPushable` |

Implement those interfaces on any object to make it interact with a `Hand`. Nothing in this
assembly references AutoHand, so the player can ship with any interaction stack.

## Driving the player

```csharp
player.Move(new Vector2(stick.x, stick.y));
player.Turn(stickTurn);
player.Jump();
player.SetPosition(position, rotation);
```

Movement, snap/smooth turning, grounding (feet first, capsule fallback), head depenetration,
crouch height, platforming and teleporting are handled by `Player`. Input is entirely up to the
active controller.
