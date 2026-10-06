using System;
using UnityEngine.Events;
using UnityEngine.XR;

namespace Nox.CCK.XR {
	/// <summary>
	/// Événements de devices XR d'un provider, alimentés par les événements d'<see cref="InputDevices"/>.
	///
	/// <para>
	/// Un provider les expose via <see cref="IXRInputProvider"/> : les consommateurs (nox.xr, le
	/// calibrage du corps entier…) s'abonnent ainsi au <b>provider</b> au lieu d'écouter InputDevices
	/// directement, et continuent de fonctionner si le provider change (AutoHand en remplace un autre,
	/// un provider de test, ...).
	/// </para>
	/// </summary>
	public sealed class XRDeviceEvents : IDisposable {
		/// <summary>Device XR apparu (casque, manette, tracker…).</summary>
		public readonly UnityEvent<InputDevice> Connected = new();

		/// <summary>Device XR disparu.</summary>
		public readonly UnityEvent<InputDevice> Disconnected = new();

		/// <summary>Configuration d'un device XR changée (rôle, modèle…).</summary>
		public readonly UnityEvent<InputDevice> ConfigChanged = new();

		private bool _subscribed;

		public XRDeviceEvents()
			=> Subscribe();

		/// <summary>S'abonne aux événements d'<see cref="InputDevices"/> (idempotent).</summary>
		public void Subscribe() {
			if (_subscribed)
				return;

			_subscribed = true;
			InputDevices.deviceConnected     += OnConnected;
			InputDevices.deviceDisconnected  += OnDisconnected;
			InputDevices.deviceConfigChanged += OnConfigChanged;
		}

		/// <summary>Se désabonne (les UnityEvents restent utilisables par les consommateurs).</summary>
		public void Unsubscribe() {
			if (!_subscribed)
				return;

			_subscribed = false;
			InputDevices.deviceConnected     -= OnConnected;
			InputDevices.deviceDisconnected  -= OnDisconnected;
			InputDevices.deviceConfigChanged -= OnConfigChanged;
		}

		public void Dispose()
			=> Unsubscribe();

		private void OnConnected(InputDevice device)
			=> Connected.Invoke(device);

		private void OnDisconnected(InputDevice device)
			=> Disconnected.Invoke(device);

		private void OnConfigChanged(InputDevice device)
			=> ConfigChanged.Invoke(device);
	}
}
