# ADR 0096 · Arbre de navigation Settings

Statut : source `7c52b3974`, suites complètes et paquet exact vérifiés ; natif source borné anglais/clair/X11. FormSettings et introduction Git N2, aucune parité globale acquise.

Retrouver les réglages comme dans Windows, sans perdre les brouillons en naviguant. Reprendre la hiérarchie, l’ordre relatif et les icônes d’origine des seules pages réellement implémentées. La racine Git Extensions héberge Checklist lorsqu’elle est disponible ; Git affiche son introduction, puis Paths et Config. Thème et langue rejoignent Appearance ; normalisation des branches et options avancées rejoignent Advanced.

Conserver les instances de pages. La dernière sélection reste en mémoire dans le processus ; une destination explicite la précède. Goto révèle la destination et peut focaliser son contenu ; la navigation normale conserve le clavier dans l’arbre. La recherche surligne sans retirer les nœuds, combine les mots-clés en AND et parcourt les résultats avec Entrée, sans déclencher OK.

Apply et SaveAll publient toutes les pages sans fermer. Après réussite, Cancel ou fermeture rendent true comme Windows ; les brouillons ultérieurs restent abandonnés. Une erreur tardive ne restaure pas les publications déjà réalisées. La notice instant-save de Checklist rappelle cette limite. Une fermeture pendant un scan ou une publication annule les acteurs connus et attend leur drainage.

Un panneau défilable horizontalement garde les pages accessibles sur fenêtre étroite. Effacer l’erreur générale après Save réussi. Fonts AcceptSaved reprend le résultat fusionné publié et sa nouvelle référence, pour qu’un Apply répété n’écrase pas une préférence concurrente déjà conservée.

Config reste un dialogue enfant avec ses propres brouillons et portées. Plugins, intégration CI, autres pages absentes et sources Global/Local de toutes les pages ne deviennent pas disponibles par ce changement d’hôte. Preuves et limites dans le [TODO](../../../todo.md) : 2006/720/35 sans skip, deux fermetures natives normales et persistance après redémarrage. Le paquet a été installé/lancé dans un banc isolé ; son interface métier n’est pas certifiée interactivement.

Repères : DesktopSettingsDialog, Controls/SettingsNavigationView, MainWindow.ToolsHelp, AppearanceFontsSettingsPage.
