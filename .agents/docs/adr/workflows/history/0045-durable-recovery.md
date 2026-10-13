# ADR 0045 · Reprise durable du cherry-pick

Statut : implémenté ; validation en cours, paquet courant dans le TODO.

Besoin : retrouver une tentative interrompue après fermeture du dialogue ou redémarrage, sans perdre l’index et les changements préexistants. Le journal privé est propre au worktree et précède Git : options, identité, fichiers/liens/modes, index et objets staged devenus non atteignables après résolution/GC. Aucune ref temporaire ne sert à les retenir.

Réouvrir attache la tentative existante, sans refaire le pick ni continuer automatiquement une sélection multiple. Un journal incomplet ou une identité différente bloque une nouvelle tentative. Sans commit, Continue conserve l’index résolu sans inventer un commit ; le mode automatique conserve le séquenceur Git.

Les destinations de déplacement de dossier sont couvertes avant Git depuis le parent sélectionné et l’index initial. Les noms imprévus restent refusés avant rollback ; ne pas lire les datasets non suivis sans rapport. Une panne IO peut interrompre la restauration de plusieurs fichiers : garder le journal Restoring, bloquer Continue et permettre de terminer Abort/Skip. La lease administrative est libérée au décès du processus.

Repères : GitRepository.CherryPickRecovery, GetCherryPickRecoveryAsync, ResumeCherryPickAsync, CherryPickDialog ; scénarios de redémarrage/GC et de déplacements dans tests/linux.
