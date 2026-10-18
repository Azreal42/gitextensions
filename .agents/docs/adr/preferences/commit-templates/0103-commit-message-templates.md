# ADR 0103 · Bibliothèque de messages Commit

Décision : bibliothèque applicative et validation portées sur un périmètre N2, source `fe1cff986`.

La bibliothèque reste distincte de Git `commit.template`. Deux onglets gèrent dix emplacements initiaux nom/texte/regex de branche et six règles : limites sujet/lignes, auto-wrap, regex, indentation et deuxième ligne vide. Zéro désactive les limites ; wrap et indentation sont actifs par défaut. L’UI propose 0–100 ; une valeur héritée supérieure est préservée avec notice jusqu’au choix explicite.

OK publie les intentions, Cancel/fermeture les abandonnent. Saved est isolé par profil et actualise les Commit ouverts ; une lecture précède le commit. La première Échap ferme le déroulant avant le dialogue. Erreurs et conflits restent visibles.

Conventional conserve le corps lors des actions d’en-tête/portée/pieds. Le garde de sélection clavier protège le mappage sous souris immobile ; il cède au mouvement réel/appui, à Échap ou à la fermeture, sans faux clic. Le registre applicatif ne charge pas les plugins Windows ; WordWrapper reste lié à l’original.

Le formatage différé respecte texte/caret/sélection/interaction/focus/IME/replay/fermeture. Adaptations : XDG JSON/fusion d’intentions, regex bornées une seconde, contrôles Classic/Avalonia. Le [suivi vérifié](../../../todo.md#bibliothèque-de-messages-commit--périmètre-vérifié) distingue ciblé, natif source borné et paquet isolé ; la référence complète069 reste historique et les variantes ouvertes explicites.
