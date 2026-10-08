using UnityEngine;

namespace Nox.XR.Trackers {
	/// <summary>
	/// Snapshot d'un tracker physique, dans l'espace de suivi du runtime qui le fournit
	/// (nox.xr applique ensuite l'origine XR pour travailler en espace monde).
	/// </summary>
	public struct TrackerPose {
		/// <summary>
		/// Identifiant stable entre les sessions : numéro de série quand le runtime en fournit un,
		/// sinon un repli « <c>name#index</c> » (l'index n'a de sens que pour la frame courante).
		/// </summary>
		public string Id;

		public Vector3    Position;
		public Quaternion Rotation;

		/// <summary>
		/// Vitesse du device, dans le même espace que <see cref="Position"/> (le runtime la fournit via
		/// <c>CommonUsages.deviceVelocity</c>). Reste à zéro quand le runtime ne l'expose pas.
		/// </summary>
		public Vector3 Velocity;

		/// <summary>Vitesse angulaire (rad/s), même espace que <see cref="Rotation"/>.</summary>
		public Vector3 AngularVelocity;

		public override string ToString()
			=> $"{Id} @ {Position}";
	}
}
