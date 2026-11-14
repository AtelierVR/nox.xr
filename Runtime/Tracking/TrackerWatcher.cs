using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Language;
using Nox.CCK.Notifications;
using Nox.Notifications;
using Nox.Settings;
using Nox.XR.Loaders;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Trackers;

namespace Nox.XR.Runtime.Tracking {
	/// <summary>
	/// Watches the trackers of the active loader: counts them for the settings readout and posts a
	/// notification when a tracker connects, disconnects, starts or stops tracking.
	/// <para>
	/// Every device present when a provider is attached is reported as a burst: the watcher stays silent
	/// during <see cref="StartupDelay"/> so starting the game does not notify once per tracker. Only the
	/// changes that follow are announced.
	/// </para>
	/// </summary>
	public static class TrackerWatcher {
		/// <summary>Notification channel the tracker events are posted to.</summary>
		public const string NotificationChannel = "xr";

		/// <summary>How long the watcher stays silent after a provider attached, in seconds.</summary>
		private const float StartupDelay = 1.5f;

		private static ITrackerProvider _provider;
		private static CancellationTokenSource _arm;
		private static bool _armed;
		private static bool _channelCreated;

		/// <summary>Last tracking state announced per tracker, to drop redundant events.</summary>
		private static readonly Dictionary<string, bool> TrackingStates = new();

		/// <summary>Trackers reported as connected, so a removed placeholder slot is not announced.</summary>
		private static readonly HashSet<string> Announced = new();

		/// <summary>Scratch set used to deduplicate a tracker exposed by several providers.</summary>
		private static readonly HashSet<string> Seen = new();

		/// <summary>Connected trackers usable for full-body tracking (HMD and hands excluded).</summary>
		public static int Connected { get; private set; }

		/// <summary>Trackers of <see cref="Connected"/> currently tracking.</summary>
		public static int Tracking { get; private set; }

		/// <summary>Trackers usable for full-body tracking, connected or not.</summary>
		public static int Total { get; private set; }

		public static void Initialize() {
			XRLoaderManager.ProviderChanged.AddListener(OnProviderChanged);
			EnsureChannel();
			Attach(XRLoaderManager.Provider?.Features?.Trackers);
		}

		public static void Shutdown() {
			XRLoaderManager.ProviderChanged.RemoveListener(OnProviderChanged);
			Attach(null);
		}

		private static void OnProviderChanged(IXRLoaderProvider loader)
			=> Attach(loader?.Features?.Trackers);

		private static void Attach(ITrackerProvider provider) {
			if (ReferenceEquals(_provider, provider))
				return;

			if (_provider != null) {
				_provider.OnRegistered.RemoveListener(OnRegistered);
				_provider.OnUnregistered.RemoveListener(OnUnregistered);
				_provider.OnTracking.RemoveListener(OnTracking);
			}

			_provider = provider;
			TrackingStates.Clear();
			Announced.Clear();

			if (_provider != null) {
				_provider.OnRegistered.AddListener(OnRegistered);
				_provider.OnUnregistered.AddListener(OnUnregistered);
				_provider.OnTracking.AddListener(OnTracking);
			}

			Arm();
			Refresh();
		}

		/// <summary>Restarts the silent window: the devices already present must not notify.</summary>
		private static void Arm() {
			_arm?.Cancel();
			_arm?.Dispose();
			_arm = new CancellationTokenSource();
			_armed = false;
			ArmAsync(_arm.Token).Forget();
		}

		private static async UniTask ArmAsync(CancellationToken token) {
			await UniTask.Delay(
				TimeSpan.FromSeconds(StartupDelay),
				DelayType.UnscaledDeltaTime,
				PlayerLoopTiming.Update,
				token
			).SuppressCancellationThrow();

			if (!token.IsCancellationRequested)
				_armed = true;
		}

		private static void OnRegistered(ITracker tracker) {
			if (!IsCandidate(tracker) || !tracker.IsConnected)
				return;

			var known = Announced.Contains(tracker.Id);
			Refresh();

			if (known || !_armed)
				return;

			Notify("connected", tracker.Name, tracker.Id);
		}

		private static void OnUnregistered(ITracker tracker) {
			if (!IsCandidate(tracker))
				return;

			var known = Announced.Remove(tracker.Id);
			TrackingStates.Remove(tracker.Id);
			Refresh();

			if (!known || !_armed)
				return;

			Notify("disconnected", tracker.Name, tracker.Id);
		}

		private static void OnTracking(ITracker tracker, bool tracking) {
			if (!IsCandidate(tracker))
				return;

			if (TrackingStates.TryGetValue(tracker.Id, out var previous) && previous == tracking)
				return;

			TrackingStates[tracker.Id] = tracking;
			Refresh();

			if (!_armed)
				return;

			// A device that starts tracking is present, even when its registration happened before the
			// watcher could see it as connected.
			if (tracking)
				Announced.Add(tracker.Id);

			Notify(tracking ? "tracking" : "untracking", tracker.Name, tracker.Id);
		}

		/// <summary>Whether the device is a full-body tracker (HMD and hands are driven by the runtime).</summary>
		private static bool IsCandidate(ITracker tracker)
			=> tracker != null && !tracker.IsHead() && !tracker.IsHand();

		/// <summary>Recomputes the counters from every available provider and refreshes the settings page.</summary>
		private static void Refresh() {
			var connected = 0;
			var tracking = 0;
			var total = 0;

			Seen.Clear();

			foreach (var provider in TrackerResolver.Providers()) {
				if (provider == null || !provider.IsAvailable)
					continue;

				var trackers = provider.Trackers;
				for (var i = 0; i < trackers.Count; i++) {
					var tracker = trackers[i];
					if (!IsCandidate(tracker) || !Seen.Add(tracker.Id))
						continue;

					total++;
					if (tracker.IsConnected)
						connected++;
					if (tracker.IsTracking)
						tracking++;

					TrackingStates[tracker.Id] = tracker.IsTracking;

					if (tracker.IsConnected)
						Announced.Add(tracker.Id);
				}
			}

			if (connected == Connected && tracking == Tracking && total == Total)
				return;

			Connected = connected;
			Tracking = tracking;
			Total = total;

			SettingsNotifier.NotifyUpdated(null);
		}

		private static void Notify(string kind, string name, string id) {
			var notifications = Notifications();
			if (notifications == null)
				return;

			EnsureChannel();

			// The runtime gives every tracker of a family the same name: the id is what tells them apart.
			var label = string.IsNullOrEmpty(name)
				? id
				: string.Equals(name, id, StringComparison.Ordinal)
					? name
					: $"{name} [{id}]";

			notifications.Notify(
				NotificationChannel,
				LanguageManager.Get($"xr.notification.tracker.{kind}.title"),
				LanguageManager.Get($"xr.notification.tracker.{kind}.body", label)
			);
		}

		private static INotificationManager Notifications()
			=> Main.CoreAPI?.ModAPI
				?.GetMod("notifications")
				?.GetInstance<INotificationManager>();

		/// <summary>Creates the channel once (idempotent on the manager side).</summary>
		private static void EnsureChannel() {
			if (_channelCreated)
				return;

			var notifications = Notifications();
			if (notifications == null)
				return;

			notifications.CreateChannel(
				NotificationChannel,
				LanguageManager.Get("xr.notification.channel"),
				NotificationImportance.Default,
				builder => builder.SetDescription(LanguageManager.Get("xr.notification.channel.description"))
			);

			_channelCreated = true;
		}
	}
}
