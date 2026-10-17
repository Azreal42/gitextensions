# ADR 0089 — Raccourcis partagés et configurables

Statut : page Hotkeys N2. Aucun changement du niveau des fenêtres métier ni N4.

Retrouver les raccourcis Windows et permettre leur personnalisation sans modifier les dépôts. Le catalogue conserve les identités numériques, l’ordre et les alternatives de neuf contextes, soit 171 presets fixes. Une collision avertit sans bloquer ; le panneau actif et le premier match du contexte décident. Une commande absente ne reçoit aucune action de substitution.

Apply/Clear/Reset modifient le brouillon ; seul OK publie. Clear conserve explicitement None et Cancel garde les touches actives. Un profil illisible reste intact et l’erreur visible. Le remplacement du JSON XDG est atomique avec détection des changements observables, sans garantie de compare-and-swap interprocessus.

Repères : `HotkeyCatalog` → `HotkeyBindings` → `HotkeyPreferencesStore` → `DesktopHotkeys`. Un même instantané alimente dispatch, menus et tooltips des fenêtres ouvertes. MainWindow.Hotkeys/Shortcuts, FileStatusListContextMenuController et les hôtes Commit/Stash/ResolveConflicts réutilisent leurs commandes existantes. Un enfant capable précède Browse/Grid ; seul son popup exact se ferme après acceptation. Les saisies texte gardent leurs gestes d’édition. Les corrections et pièges sont dans [failure](../../../failure.md).

Restent ouverts : fournisseurs ConventionalCommit ; Browse CI/output/sous-module/superproject ; sélection multiple LeftPanel ; fork-point Grid ; TreatFileAsText et ResetLines hors Commit ; Visual Studio ; moteur/catalogue Scripts et import Windows. ToggleOrderRevisionsByDate a un preset sans case d’exécution dans la source Windows actuelle.

Source `89672b5c6370a7752d6db441445d5381eb4d58dd` : [full04](../../../../../artifacts/linux/configurable-hotkeys-20261017/full-04/parent-verification.json), **1686/1686 Desktop, 675/675 Core, 35/35 Plugins** ; [paquet exact](../../../../../artifacts/linux/snapshots/89672b5c6/proofs/package-parent-verification.json). Les [cas antérieurs sont conservés](../../../../../artifacts/linux/configurable-hotkeys-20261017/full-04/hotkeys-case-retention.json). Les échecs [full01](../../../../../artifacts/linux/configurable-hotkeys-20261017/full-01/parent-failure-review.json) et [full03](../../../../../artifacts/linux/configurable-hotkeys-20261017/full-03/parent-failure-review.json) restent consultables sans acceptation transférée.

[Natif borné](../../../../../artifacts/linux/snapshots/89672b5c6/proofs/native-hotkeys-review.json) : GoToCommit remappé puis None, Apply/Cancel/OK, deux menus actualisés, persistance et première flèche sans réparation, dans une instance Browse anglais/clair/X11 privé. Cela ne valide pas tous les contextes, les erreurs IO/collisions, FR/Dark, Wayland/DPI ou l’accessibilité. Les rendus et ciblés historiques gardent leur source propre ; les comptes recouvrants ne s’additionnent pas. Les treize familles globales restent ouvertes.
