# Failure

| Observation | Cause | Sortie / statut |
| --- | --- | --- |
| `dotnet --info` via `/snap/bin/dotnet` échoue dans le sandbox ; aucun SDK réel installé | Lanceur Snap / confinement | SDK officiel 10.0.401 installé dans `.dotnet`, utiliser ce binaire. Résolu. |
| `global.json` : version `10.0` invalide | Version SDK incomplète | `10.0.100` + `latestFeature`. Résolu. |
| Exécutable actuel cible `net10.0-windows` et WinForms | Architecture Windows, pas un défaut de packaging | UI et frontières OS à porter ; chantier ouvert. |
| Téléchargement via `curl` impossible | Outil absent | Utiliser `wget` ; SDK installé. Résolu. |
| Actions UI cliquables pendant un refresh, clic suivant perdu | Verrou busy sans état visuel | Désactiver actions pendant l'opération ; attendre idle dans tests. Corrigé, testé. |
| Stash de nom contenant newline laissé en place malgré exit 0 | Option globale `--literal-pathspecs` incompatible avec `stash push -u` | Limiter cette option aux commandes à pathspec ; test réel. Résolu. |
| Version Debian contient le chemin HOME | Expansion Bash de `~` dans substitution | Citer le caractère ; paquet vérifié/reproductible. Résolu. |
| Chemins Linux à octets non UTF8 | API .NET basée sur string UTF8 | Unicode valide couvert ; roundtrip d'octets invalides restant. Ouvert. |
| Installation APT locale non exécutée | sudo nécessite authentification, pas de conteneur installé | Résolution APT simulée et paquet exécuté après extraction ; installation réelle reste un gate CI. Ouvert. |
| Reparenting du filtre historique plante après reprise du layout original | Contrôle encore attaché au panel précédent | Détacher avant insertion ; parcours UI validé. Résolu. |

Ne conserver ici que les erreurs qui changent la suite du travail ou évitent de refaire une impasse.
