# ADR 0083 — Informations de commit partagées

## Intention

Comprendre le commit sélectionné et retrouver les branches/tags qui le contiennent, depuis Browse, l’historique fichier et Blame, sans checkout ni lecture globale de la grille. Une lecture passive ne doit pas prendre le focus ni publier le résultat d’une ancienne sélection.

## Décision

- Partager `CommitInformationView` entre les trois hôtes, avec détails complets du commit, notes et messages des seuls tags annotés directement attachés. Distinguer les références contenant le commit du tag ancêtre trouvé par `describe --first-parent` ; conserver le message complet sans reconstruire ses lignes depuis le sujet.
- Garder le comparateur Windows de CommitInfo distinct de ceux de l’arbre, des badges et de QuickRef : checkout courant, locales prioritaires, distantes prioritaires, autres locales puis autres distantes ; priorité de branche puis de remote, départage ordinal. Le filtre RemoteIfNoLocal reste un scan séquentiel : une locale coupe les distantes suivantes sans effacer celles déjà émises. Le nom historique de tri reste séparé de l’identité typée qui porte l’action, y compris pour les noms visuellement identiques et le mode remote seul.
- Conserver les six préférences indépendantes. Défauts actifs : locales, tags, messages annotés et tag ancêtre ; distantes et fallback distantes désactivés. Show all est propre au type et à la sélection : jusqu’à douze références affichées, toutes restent visibles ; au-delà, dix puis Show all, sans nouvelle requête Git lors de l’expansion.
- Naviguer vers le commit de la référence complète ; ne pas déduire une mutation d’un libellé. Le HEAD détaché reste identifié séparément dans les données Core. Les options du menu n’ont pas d’icônes sous Windows.
- Charger seulement le panneau visible, avec une génération par dépôt/révision/options. Annuler les anciens lecteurs et empêcher leur publication ; sceller les nouvelles actions avant d’attendre toutes les lectures lors d’une fermeture. Conserver l’origine du clavier lors de la lecture et du dépliage. Une nouvelle lecture CommitInfo ne peut effacer que le diagnostic qu’elle a elle-même publié ; conserver les erreurs d’une autre lecture ou opération.

## Adaptations et limites

Les regex gardent `ExplicitCapture` et les priorités partagées ; la validation complète et le timeout de matching sont les adaptations Linux existantes. Les actions utilisent les identités complètes même lorsque le texte Windows est ambigu.

Liens associés configurables, avatar de CommitInfo, rendu Markdown et liens mailto restent ouverts, ainsi que l’audit natif étendu des hôtes/variantes. Les avatars du lecteur Blame ne prouvent pas un avatar dans CommitInfo. Cette tranche ne change ni niveau d’écran, ni nombre de fenêtres, ni clôture de famille.

## Repères et validation

`GitCommitInformation`, `GitRepository.CommitInformation`, `CommitInformationPresentation`, `CommitInformationPreferencesStore`, `CommitInformationView`, `MainWindow.BrowserReads`, `FileHistoryDialog` et `BlameDialog`. Oracles/tests : `CommitInformationScenarios`, `CommitInformationPresentationTests`, `CommitInformationViewTests` et `CommitInformationIntegrationTests`. Le code reste la source de vérité.

Source produit `b356d5f6713de70ca744ca82cb07a04b59e74f7b` : [Full02 vérifié](../../../../../artifacts/linux/commit-information-20261017/full-02/parent-verification.json), **1402/1402 Desktop**, **625/625 Core**, **35/35 Plugins**, sans retrait de cas anciens. Le [paquet exact vérifié](../../../../../artifacts/linux/snapshots/b356d5f67-package03/proofs/log-finalized-parent-verification-05/package-parent-verification.json) conserve cette source, ses cinq phases à 0 et une extraction complète conforme. Le [natif ciblé](../../../../../artifacts/linux/snapshots/b356d5f67/proofs/native-commit-information-parent-review.json) est accepté pour **Browse seul, une session anglais/clair sous X11 privé** : premier clic, premières Up/Down sans réparation, six options et Remote ON, Show all branches de dix à seize sans nouvelle lecture Git dans la fenêtre observée, retour au dashboard et fermeture normale.

En natif, FileHistory/Blame, autres dispositions/thèmes/locales, Show all tags, Wayland, DPI et accessibilité restent non vérifiés. Les trois relevés AT-SPI sont indisponibles ; ils ne certifient aucun arbre accessible.

Les [essais refusés](../../../failure.md#commitinfo--diagnostics-et-fermeture) restent conservés avec leur source. Les tests attendent la fermeture effective et le drainage avant de retirer le profil ou le dépôt temporaire ; une échéance de nettoyage est un échec.
