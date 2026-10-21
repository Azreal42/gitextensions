# ADR 0125 — Régler les confirmations des workflows existants

Présenter ensemble les dix-sept choix Windows et leurs vrais consommateurs, avec inversion des booléens et triétats d’origine : suppression non fusionnée, checkout depuis le panneau gauche, stash exact après checkout réussi et seconde question Reset complètent les treize choix antérieurs.

La page écrit les profils XDG des workflows existants. ConfirmationsSettingsSession fusionne uniquement les champs modifiés, conserve les inconnus et refuse les conflits d’intention ou d’octets observés. No-op et Cancel n’écrivent pas. Un fichier acquitté reste durable si un profil suivant échoue ; Apply n’est pas une transaction globale et Cancel n’annule pas une publication acquise.

Les consommateurs relisent leur autorité commune : une page de préférences seule ne suffit pas à prouver les confirmations métier. N2 partiel ; [preuves et gestes vérifiés](../../../todo.md#portable-workflows--perimetre-verifie).

Le complément garde le mapping positif ConfirmBranchCheckout, les inversions DontConfirm et le triétat autopop ; dismiss/annulation ne mémorisent aucun consentement. [ConfirmationsFourPoliciesTests](../../../../../tests/linux/GitExtensions.Desktop.Tests/ConfirmationsFourPoliciesTests.cs) est exécuté dans les suites complètes de ce lot ; audit natif étendu encore ouvert.

Le clic simple dans le panneau gauche rejoint le commit de la référence sans changer le filtre ni reconstruire le graphe, comme GoToRef Windows. Le double clic reste une intention de checkout distincte ; sa lecture préalable est annulée avant une opération de dépôt ou fermeture.
