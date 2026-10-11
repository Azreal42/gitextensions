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
| Installation APT locale non exécutée | sudo nécessite authentification, pas de conteneur installé | DPKG installé/lancé dans une racine isolée avec dépendances host ; APT sur OS vierge reste un gate CI. Ouvert. |
| Reparenting du filtre historique plante après reprise du layout original | Contrôle encore attaché au panel précédent | Détacher avant insertion ; parcours UI validé. Résolu. |
| Ouverture bare s’arrête après chargement de l’historique | `stash list` exige un worktree même en lecture | Lire le reflog de refs/stash ; test UI d’ouverture complète. Résolu. |
| Tableau plugin plante après suppression | Template Avalonia reçoit un élément null pendant le renouvellement | Garde null ; aperçu/annulation/suppression réelle validés. Résolu. |
| Badges du graphe gardent les couleurs claires | Rendu non invalidé au changement de thème | Invalidation et test de pixels clair/sombre. Résolu. |
| Indexer/désindexer tout touche des fichiers masqués par le filtre Commit | Action globale au lieu des éléments visibles | Chemins visibles uniquement, scénario avec index partiel et noms littéraux. Corrigé, testé. |
| Indexer un renommage modifié échoue : ancien pathspec absent | Ancien et nouveau noms transmis à git add après git mv | Indexer le nom courant ; désindexer les deux noms. Corrigé, test UI réel. |
| Sélections X11 restent orange malgré les palettes reprises | Accent OS encore consommé par les templates Fluent | Ressources des templates et texte sélectionné repris ; tests pixels/contraste et captures X11 claire/sombre. Résolu. |
| Annulation d’un outil externe rend la main avant la fin du processus | WaitForExit annulé ne collecte pas le processus tué | Tuer l’arbre puis attendre la sortie réelle ; test de PID. Résolu. |
| Impact conserve un fond clair en thème sombre | Recherche de ressource sans variante effective | Résoudre avec ActualThemeVariant ; pixels clairs/sombres testés. Résolu. |
| Petite image de hauteur impaire rendue floue | Centrage interne sur un demi-pixel | Dimensionner avant centrage et arrondir le layout ; taille/position mesurées sur les pixels. Résolu. |
| Tests de cancellation échouent pendant la lecture de procfs | Le descendant disparaît entre File.Exists et ReadLines | Relecture après IOException, état absent/zombie exigé et délai borné ; 54 scénarios passent. Résolu. |
| Skip am avance mais la ligne suivante est marquée appliquée | Un second conflit produit exit non nul après avancement | Relire état am et repérer les indices ; série à deux conflits testée. Résolu. |
| Tests Avalonia bloqués malgré opération annulée et fenêtre prête à fermer | Assert.ThrowsAsync attend synchroniquement une continuation capturée sur le dispatcher UI | Attendre normalement dans le test async, avec délai borné ; tests History/Blame et HTTP concernés. Résolu. |
| Le clonage réinitialise le sous-dossier saisi | TextChanged différé arrive après l’édition utilisateur | Observer TextProperty immédiatement ; clone réel vers le dossier choisi testé. Résolu. |
| Nom de clone encodé et avertissement moins lisible que l’original | URL non décodée, deux-points local traité comme SCP et couleur fixe | Distinguer URL/SCP/local, reprendre adaptation de contraste originale et templates XLF complets ; scénario rendu clair/sombre/français. Résolu. |
| Supprimer une référence sélectionnée dans la navigation bloque le refresh | Le filtre de révision automatique vise la ref disparue | Marquer les filtres de navigation et revenir à All quand la ref disparaît ; auteur/message/FirstParent conservés, saisie manuelle inchangée. Scénarios réels verts. |

Ne conserver ici que les erreurs qui changent la suite du travail ou évitent de refaire une impasse.
