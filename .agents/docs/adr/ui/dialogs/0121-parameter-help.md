# ADR 0121 — Aide de paramètres réutilisable

Consulter les arguments de scripts sans quitter leur brouillon ni préparer une exécution.

`SimpleHelpDisplayDialog` reprend la feuille Windows 299×439 : texte traduit readonly, sélectionnable, multiligne, retour à la ligne, défilement vertical et palette Info. Titre et contenu restent littéraux ; les liens du texte ne sont pas activés automatiquement.

Scripts ouvre une fenêtre non modale et réactive la même tant qu'elle est visible. La sélection survit au changement de focus, avec highlight inactif masqué. La fenêtre se ferme avec Settings ou au retrait de la page Scripts, comme OnParentChanged Windows ; le brouillon est conservé. Une nouvelle ouverture crée une feuille après fermeture. Repère : `ScriptsSettingsPage.ShowHelp`.
