using System;
using System.Linq;
using Nox.Avatars.StateMachines;
using Nox.CCK.Avatars.Playable;
using Nox.CCK.Entities;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Takes the tracked limbs out of the locomotion animation: the <c>Locomotion</c> layer
	/// keeps running and posing the body, but the parts a tracker drives are masked out of it, so nothing animates
	/// them over the IK.
	/// <para>
	/// That is the per-limb behaviour a full-body setup needs: a tracked leg stops following the walk cycle while an
	/// untracked one keeps it, and without a pelvis tracker the whole body stays animated. Nothing is baked in the
	/// avatar: the mask is built at runtime and pushed on the layer through the Playable API, so an avatar that was
	/// already uploaded follows the rule without being uploaded again.
	/// </para>
	/// <para>
	/// Driven by the part and module lifecycles, never by a per-frame check.
	/// </para>
	/// </summary>
	public partial class XRControllerV2 {
		/// <summary>
		/// Rig slot → humanoid body parts masked out of the locomotion animation when that slot is tracked.
		/// <para>
		/// A torso tracker (hips, spine, chest, neck) takes the whole column up to the head away from the animation:
		/// the spine is solved between the pelvis and the head by the IK, so letting the layer animate it would fight
		/// both. An arm or a leg only masks its own limb.
		/// </para>
		/// </summary>
		private static readonly (EntityRig slot, AvatarMaskBodyPart[] parts)[] _maskedSlots = {
			(EntityRig.Hips,  new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head }),
			(EntityRig.Spine, new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head }),
			(EntityRig.Chest, new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head }),
			(EntityRig.Neck,  new[] { AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head }),
			(EntityRig.LeftHand,  new[] { AvatarMaskBodyPart.LeftArm }),
			(EntityRig.RightHand, new[] { AvatarMaskBodyPart.RightArm }),
			(EntityRig.LeftFoot,  new[] { AvatarMaskBodyPart.LeftLeg }),
			(EntityRig.RightFoot, new[] { AvatarMaskBodyPart.RightLeg })
		};

		/// <summary>Applies the locomotion mask matching the parts that are tracked right now.</summary>
		public void RefreshFullBodyOverride() {
			var layers = _avatar?.Descriptor?.GetModules<IPlayableLayerModule>().FirstOrDefault();
			if (layers == null)
				return;

			var layer = FindLocomotionLayer(layers);
			if (layer < 0)
				return;

			var mask = BuildLocomotionMask();
			if (!mask)
				return;

			layers.SetLayerMask(layer, mask);

			Logger.LogDebug($"Locomotion masked out: {TrackedSlotsLabel()}.", this, tag: nameof(XRControllerV2));
		}

		/// <summary>
		/// Mask of the locomotion layer: every humanoid part is kept, except the ones a tracker drives right now.
		/// </summary>
		private AvatarMask BuildLocomotionMask() {
			if (!player)
				return null;

			var mask = new AvatarMask();

			foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart))) {
				if (part == AvatarMaskBodyPart.LastBodyPart)
					continue;

				mask.SetHumanoidBodyPartActive(part, true);
			}

			for (var i = 0; i < _maskedSlots.Length; i++) {
				var (slot, parts) = _maskedSlots[i];
				if (!IsTracked(slot))
					continue;

				for (var p = 0; p < parts.Length; p++)
					mask.SetHumanoidBodyPartActive(parts[p], false);
			}

			return mask;
		}

		/// <summary>Tracked slots, for the log line.</summary>
		private string TrackedSlotsLabel() {
			var tracked = _maskedSlots
				.Where(slot => IsTracked(slot.slot))
				.Select(slot => slot.slot.ToString())
				.ToArray();

			return tracked.Length == 0 ? "no tracked part" : string.Join(", ", tracked);
		}

		/// <summary>Whether a part drives the rig slot right now.</summary>
		private bool IsTracked(EntityRig slot)
			=> player.Parts.TryGetValue(slot, out var part) && part && part.PartEnabled;

		/// <summary>Index of the locomotion layer, by role first (any key), then by key.</summary>
		private static int FindLocomotionLayer(IPlayableLayerModule layers) {
			var name = PlayableLayerNaming.Locomotion.ToString();

			for (var i = 0; i < layers.LayerCount; i++) {
				if (string.Equals(layers.GetLayerRole(i), name, StringComparison.OrdinalIgnoreCase))
					return i;
			}

			return layers.FindLayer(name);
		}
	}
}
