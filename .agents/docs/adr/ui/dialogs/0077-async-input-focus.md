# ADR 0077 · Focus pendant les lectures d’un dialogue

Statut : périmètre testé sur 189c18a6c ; première frappe native après chargement retardé vérifiée. Retour du focus au navigateur après Cancel encore ouvert.

Besoin : les champs temporairement désactivés pendant une lecture Git doivent recevoir la première frappe dès la fin de l’initialisation.

Décision : RepositoryDialog garde le contrôle réellement focalisé appartenant à ses inputs suivis, y compris un descendant éditable. Il le restaure après UpdateActions uniquement si la fenêtre et le contrôle sont encore utilisables, sans reprendre le focus à un autre contrôle ou sous-dialogue. Une fermeture demandée exclut cette restauration ; aucune activation OS ni rappel générique à la fermeture d’un menu.

Repères : RepositoryDialogs.RunAsync/RestoreInputFocus, CreateBranchInitializationFocusTests ; RevisionContextFocusTests attend désormais la fin effective d’initialisation. Le rouge déterministe utilise une vraie lecture Git bloquée ; initialisation rapide seule ne suffisait pas à prouver ce contrat. Preuves et limites dans le TODO.
