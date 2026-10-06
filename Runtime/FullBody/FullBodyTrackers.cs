using System.Collections.Generic;
using Nox.CCK.XR;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;

namespace Nox.XR.Runtime.FullBody {
	/// <summary>
	/// Trackers utilisés par le suivi du corps entier.
	///
	/// <para>
	/// Les trackers viennent des loaders XR (<see cref="IXRLoaderProvider.Trackers"/>), comme les
	/// entrées viennent de <see cref="IXRLoaderProvider.Binding"/> : nox.xr ne connaît aucun runtime.
	/// Les loaders de <b>tous</b> les mods chargés sont interrogés (pas seulement celui qui pilote la
	/// XR) puis dédupliqués par numéro de série, donc un runtime peut compléter les trackers qu'un
	/// autre ne voit pas.
	/// </para>
	///
	/// <para>
	/// Les devices du runtime XR actif (<see cref="XRInputDevicesTrackerProvider"/>) sont ajoutés en
	/// dernier : c'est le repli générique, qui couvre les runtimes dont le loader n'expose pas de
	/// <see cref="ITrackerProvider"/>.
	/// </para>
	/// </summary>
	public static class FullBodyTrackers {
		/// <summary>
		/// Fournisseurs interrogés, dans l'ordre de lecture : les loaders connus (runtime actif en
		/// premier) puis le repli générique des devices XR.
		/// </summary>
		public static List<ITrackerProvider> Providers() {
			var providers = new List<ITrackerProvider>();

			foreach (var loader in XRLoaderManager.Providers) {
				ITrackerProvider trackers;
				try {
					trackers = loader?.Trackers;
				} catch (System.Exception) {
					// Un mod tiers ne doit pas casser la lecture des trackers des autres.
					continue;
				}

				if (trackers != null && providers.TrueForAll(p => p.Id != trackers.Id))
					providers.Add(trackers);
			}

			providers.Add(XRInputDevicesTrackerProvider.Instance);
			return providers;
		}

		/// <summary>Whether <paramref name="device"/> can be used as a full-body tracker, and why not when it cannot.</summary>
		public static bool IsCandidate(InputDevice device, bool excludeHanded, bool excludeControllers, out string reason)
			=> XRInputDevicesTrackerProvider.IsCandidate(device, excludeHanded, excludeControllers, out reason);

		/// <summary>Same as <see cref="IsCandidate(InputDevice, bool, bool, out string)"/> with controllers excluded.</summary>
		public static bool IsCandidate(InputDevice device, bool excludeHanded, out string reason)
			=> IsCandidate(device, excludeHanded, true, out reason);

		/// <summary>
		/// Transform dans lequel les poses des devices XR sont exprimées : le <i>camera floor offset</i>
		/// de l'<see cref="XROrigin"/> (l'objet dont la caméra XR est l'enfant), et non la racine du rig.
		/// <para>
		/// La distinction est essentielle : la racine du rig peut être à un endroit du monde arbitraire
		/// (elle ne bouge pas quand le joueur marche, c'est le rig qui la déplace), alors que les poses
		/// des devices sont relatives à l'espace de suivi — celui du <c>TrackedPoseDriver</c> de la
		/// caméra. Passer par la mauvaise racine plaçait les trackers à plusieurs mètres des bones.
		/// </para>
		/// </summary>
		public static Transform TrackingSpace() {
			var origin = XROriginSetter.GlobalOrigin;
			if (!origin)
				return null;

			return origin.CameraFloorOffsetObject
				? origin.CameraFloorOffsetObject.transform
				: origin.transform;
		}

		/// <summary>
		/// Fills <paramref name="into"/> with every tracker of every available provider, in world space.
		/// <para>
		/// Trackers are deduplicated by serial across providers, in reading order: a tracker one
		/// provider already reported is never added a second time by another one.
		/// </para>
		/// </summary>
		public static void Get(List<TrackerPose> into, bool excludeHanded = true, bool excludeControllers = true) {
			into.Clear();

			var origin = TrackingSpace();

			foreach (var provider in Providers()) {
				if (provider == null || !provider.IsAvailable)
					continue;

				var start = into.Count;
				provider.Get(into, excludeHanded, excludeControllers);
				RemoveDuplicates(into, start);
				ToWorldSpace(into, start, origin);
			}
		}

		/// <summary>
		/// Ramène dans l'espace monde les poses des entrées à partir de <paramref name="from"/> : les
		/// providers publient l'espace de suivi de leur runtime, l'origine XR appartient à nox.xr.
		/// </summary>
		private static void ToWorldSpace(List<TrackerPose> trackers, int from, Transform origin) {
			if (!origin)
				return;

			for (var i = from; i < trackers.Count; i++) {
				var tracker = trackers[i];
				tracker.Position = origin.TransformPoint(tracker.Position);
				tracker.Rotation = origin.rotation * tracker.Rotation;
				trackers[i]       = tracker;
			}
		}

		/// <summary>Drops the entries from <paramref name="from"/> already present earlier in the list (same serial).</summary>
		private static void RemoveDuplicates(List<TrackerPose> trackers, int from) {
			for (var i = trackers.Count - 1; i >= from; i--) {
				var id = trackers[i].Id;
				if (string.IsNullOrEmpty(id))
					continue;

				for (var j = 0; j < i; j++) {
					if (!string.Equals(trackers[j].Id, id, System.StringComparison.OrdinalIgnoreCase))
						continue;

					trackers.RemoveAt(i);
					break;
				}
			}
		}
	}
}
