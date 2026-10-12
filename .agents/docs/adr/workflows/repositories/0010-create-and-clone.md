# ADR 0010 · Création et clonage

Statut : accepté.

Besoin : retrouver les choix du clonage original et créer un dépôt central partagé sans reconstruire les commandes Git à la main.

Décision : conserver les champs source/destination/sous-dossier/branche, personnel/bare, sous-modules et historique complet. Le clone limité garde les autres branches distantes (`--no-single-branch`). « Sans checkout » et bare n'initialisent pas de sous-modules. Le dépôt central utilise `--bare --shared=all` ; la branche initiale respecte la configuration Git. Fermer le dialogue annule Git et ses descendants avant de rendre la main.

Repères : `FormClone`, `FormInit`, `CloneDialog`, `InitDialog`, `GitRepository.CloneInit`, `CloneInitDialogTests`. Préférences sous XDG ; seuls destination et choix des sous-modules sont conservés.
