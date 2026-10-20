# 0132 — Proposer une mise à jour Linux publiée

Statut : recherche manuelle et téléchargement implémentés ; [validation et limites](../../../todo.md#mises-à-jour-linux--périmètre-vérifié).

Help ouvre la fenêtre compacte originale et ses liens traduits. `LinuxUpdateClient` lit uniquement les releases publiques du fork, sans identifiants CI/hébergement. Un paquet doit correspondre au tag, à sa version Debian et à l’architecture ; absence de publication et erreur de lecture restent distinctes. Le choix de préversions du dialogue est initialisé par la préférence Advanced `CheckForReleaseCandidates`, puis reste local à la session. Une application alpha ne l’active pas implicitement ; le flag GitHub ou un tag SemVer de préversion suffit à classer la publication comme telle.

L’ordre est sémantique, jamais fondé sur le SHA ou la date. Une publication suivante peut employer `v7.2.1-linux-alpha.1` et `gitextensions_7.2.1~linux-alpha.1_amd64.deb` ; les snapshots `7.2.1-linux-alpha+SHA` ne constituent pas des versions successives. La release doit être publiée séparément : les artifacts CI ne sont pas le flux Updates.

Le téléchargement intégré vérifie taille et SHA-256 de l’asset GitHub avant publication dans un sous-dossier privé du cache. Sans digest, seuls les liens externes sont proposés. « Ouvrir le paquet » est un second geste, remis au bureau ; il ne prouve pas l’installation et ne ferme pas l’application. Aucun MSI ni runtime .NET Desktop requis pour le paquet autonome.

Requêtes/corps/redirections sont bornés ; fermer annule puis attend l’opération avant destruction du client. `UpdatesDialog`, `LinuxReleaseVersion`, `MainWindow.ShowUpdatesAsync` ; `LinuxUpdateClientTests`, `UpdatesDialogTests`, `UpdatesMenuIntegrationTests`, `ClassicMenuTests`.

Le contrôle hebdomadaire et ses préférences partagées sont décrits dans [ADR0133](0133-weekly-update-admission.md). Restent la publication initiale, l’installation vérifiée et l’audit natif étendu. Le [contrat des assets GitHub](https://docs.github.com/en/rest/releases/assets) définit `digest` et les liens de téléchargement.
