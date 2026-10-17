# ADR 0090 — Choix de langue et préférence commune

Statut : sélecteur de langue N2 ; Appearance N1 inchangée, aucun N4.

Proposer les traductions Windows installées, puis ouvrir l'application dans la langue choisie. Tout profil interactif valide sans langue, ancien ou nouveau, passe une fois par le sélecteur de langue ; clic ou Entrée accepte, fermer sans choisir conserve un choix récent ou enregistre English. Les noms XLF Windows sont les identités persistées. Les satellites ne sont pas proposés ; English reste disponible sans traduction.

La préférence est appliquée avant la construction des catalogues. Un override valide `GITEXTENSIONS_UI_CULTURE` garde priorité ; le formatage OS reste inchangé. Version/aide/smoke et helpers ne montrent pas le sélecteur de langue. Une lecture de profil invalide laisse le diagnostic existant et ses octets intacts. Une traduction retirée utilise English sans réécrire son identité ; seul un vrai nouveau choix la remplace.

Settings conserve Cancel/OK et le libellé Windows « redémarrage requis ». Pas de retraduction Browse à chaud. Les sauvegardes thème/tri/plugin relisent les préférences récentes et ne remplacent que leur champ. Settings préserve une langue modifiée depuis son ouverture si le brouillon n'a pas changé ce choix ; aucune transaction interprocessus garantie.

Repères : `LanguageCatalog` → `DesktopLanguage` → `App`/`AskpassApplication` ; `ChooseLanguageDialog`, `DesktopSettingsDialog` et `PreferencesStore`. Fonts/EOL et Help Translate restent ouverts, ainsi que l'audit natif étendu des traductions/helpers/erreurs/concurrence et Wayland/DPI/accessibilité.

Source `3dd2fa83b17c5852269f3c3cd83ee073f0762c15` : [suites complètes](../../../../../artifacts/linux/language-preferences-20261017/full-01/parent-verification.json), **1719/1719 Desktop, 675/675 Core, 35/35 Plugins** ; [paquet exact](../../../../../artifacts/linux/snapshots/3dd2fa83b/proofs/package-parent-verification.json). [Natif borné](../../../../../artifacts/linux/snapshots/3dd2fa83b/proofs/native-language-review.json) Light/X11 privé : choix French réellement consommé avant Browse, English Cancel/OK puis réouverture Settings, langue de la fenêtre existante conservée. L'application d'English après redémarrage complet n'est pas exercée. Historique des régressions et limites dans [failure](../../../failure.md) ; treize familles globales toujours ouvertes.
