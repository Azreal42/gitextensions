# ADR 0125 — Régler les confirmations des workflows existants

Présenter ensemble les intentions de confirmation déjà consommées par le portable, sans cases factices. ConfirmationsSettingsPage expose 13 des 17 choix Windows, avec inversion des booléens et triétats d’origine. Suppression de branche non fusionnée, confirmation du checkout, autopop après checkout et seconde confirmation d’abort restent absents.

La page écrit les profils XDG des workflows existants. ConfirmationsSettingsSession fusionne uniquement les champs modifiés, conserve les inconnus et refuse les conflits d’intention ou d’octets observés. No-op et Cancel n’écrivent pas. Un fichier acquitté reste durable si un profil suivant échoue ; Apply n’est pas une transaction globale et Cancel n’annule pas une publication acquise.

Les consommateurs relisent leur autorité commune : une page de préférences seule ne suffit pas à prouver les confirmations métier. N2 partiel ; [preuves et gestes vérifiés](../../../todo.md#portable-workflows--perimetre-verifie).
