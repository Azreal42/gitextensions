# ADR 0029 · Gestes et rendu du lecteur original

Statut : accepté ; validation locale réussie ; paquet natif suivant à valider.

Besoin : conserver les repères du lecteur Git Extensions, jusque dans les petits gestes de lecture.

Décision : la barre apparaît immédiatement au survol, flotte à droite sans prendre une ligne de texte et garde l’ordre historique de ses commandes. Le suffixe déborde dans un menu sur les panneaux étroits. Invisibles et syntaxe restent des choix locaux ; Remember et SaveDefault suivent les règles des autres options du lecteur. Le fichier entier garde sa grammaire automatique.

Les marqueurs d’espace, de tabulation et de fin de ligne reprennent glyphes, fontes et couleurs de l’original, y compris sur les longues lignes. Ils ne modifient ni texte, ni délimiteurs, ni positions de navigation. X appartient au texte du diff partiel. Dans Commit, Échap ferme Find seul, puis efface une sélection du diff avant de fermer le dialogue et conserver le brouillon.

Repères : `FileViewer.Designer.cs`, `FileViewer.cs`, `FileViewerInternal.cs`, `externals/ICSharpCode.TextEditor/Project/Src/Gui/TextView.cs` ; côté Linux : `DiffViewerToolbar`, `DiffViewerRenderingMenu`, `DiffViewerNonprintingRenderer`, `CommitDialog`. Les tests de survol, popup, texte et hôtes vérifient ces gestes.
