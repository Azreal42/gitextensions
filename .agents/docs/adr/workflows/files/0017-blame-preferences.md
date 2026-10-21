# ADR 0017 · Préférences et contexte du blâme

Statut : accepté.

Besoin : retrouver les options d'attribution et d'affichage, les informations auteur/committer et les actions sur la ligne choisie.

Décision : réglages XDG partagés entre Blame et l'historique fichier ; reprendre les défauts et traductions originales. Les informations de commit se chargent à la demande, sans bloquer la lecture du fichier ; fermeture/rechargement annulent les lectures. Le clic droit vise sa propre ligne, indépendamment du caret.

Repères : `BlamePreferencesStore`, `BlameDialog`, `BlameView`, raccords `FileHistoryDialog`.

Complément vérifié dans ce lot : page globale Blame viewer dans Settings, dix choix Query/Display et leurs avertissements/traductions Windows. Apply/Cancel partage le profil existant avec fusion des seuls champs édités ; auteur/date/heure restent indépendants et histogram garde sa place General/menu. Les nouveaux lecteurs consomment les valeurs acceptées, sans reconfiguration forcée des lecteurs ouverts. [Page tests](../../../../../tests/linux/GitExtensions.Desktop.Tests/BlameViewerSettingsPageTests.cs) / [session tests](../../../../../tests/linux/GitExtensions.Desktop.Tests/BlameViewerSettingsSessionTests.cs) sont exécutés dans les suites complètes de ce lot.

Une fermeture différée peut laisser le lecteur attaché pendant le drain : retirer les images publiées avant leur destruction et arrêter les renderers disposés. Le menu Show changes des trois hôtes réutilise le modal CommitDiff sans déplacer la sélection du lecteur.
