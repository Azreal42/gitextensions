# ADR 0100 · Présentation commune des chemins

Les quatre choix Windows de troncature doivent agir dans les listes existantes sans changer la cible Git, le tooltip complet, la sélection ou le focus. L’enum original est lié ; Desktop conserve la préférence par profil. Apply publie la valeur réellement sauvegardée, même si un profil suivant échoue ; Cancel abandonne le brouillon.

`FilePathPresentation` sépare texte et identité ; `FilePathHeader` mesure la police et le viewport réels. Browse, File tree, comparaison, stash et les deux listes Commit partagent ce composant. Un changement de présentation actualise les runs en place. Seul un changement réel d’appartenance au filtre reconstruit les lignes ; une sélection devenue invalide déclenche la lecture du nouveau diff. Le filtre local historique reste actif en complément du choix global Filename only.

Compact adapte le contrat documenté de PathCompactPathEx aux chemins POSIX et protège les graphèmes ; égalité exacte aux coupes Win32 non promise. Le suffixe de statut submodule manque encore au modèle Linux.

[Validation ciblée](../../../../../artifacts/linux/truncate-path-20261018/review/root-targeted-import-01.json) : 278/278, dont 192 cas historiques retenus et 86 nouveaux ; build sans avertissement. Deux attentes historiques adaptées explicitement, échecs et sorties du banc conservés. Full, paquet exact et parcours natif de ce lot restent à vérifier ; aucun niveau d’inventaire supplémentaire acquis.
