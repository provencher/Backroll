
GGPOErrorCode
ggpo_start_session(GGPOSession **session,
                   GGPOSessionCallbacks *cb,
                   const char *game,
                   int num_players,
                   int input_size,
                   int localport)
{
   *session= (GGPOSession *)new Peer2PeerBackend(cb,
                                                 game,
                                                 localport,
                                                 num_players,
                                                 input_size);
   return GGPO_OK;
}

GGPOErrorCode
ggpo_start_synctest(GGPOSession **ggpo,
                    GGPOSessionCallbacks *cb,
                    char *game,
                    int num_players,
                    int input_size,
                    int frames)
{
   *ggpo = (GGPOSession *)new SyncTestBackend(cb, game, frames, num_players);
   return GGPO_OK;
}


GGPOErrorCode
ggpo_add_local_input(GGPOSession *ggpo,
                     GGPOPlayerHandle player,
                     void *values,
                     int size)
{
   if (!ggpo) {
      return GGPO_ERRORCODE_INVALID_SESSION;
   }
   return ggpo->AddLocalInput(player, values, size);
}

GGPOErrorCode ggpo_start_spectating(GGPOSession **session,
                                    GGPOSessionCallbacks *cb,
                                    const char *game,
                                    int num_players,
                                    int input_size,
                                    int local_port,
                                    char *host_ip,
                                    int host_port)
{
   *session= (GGPOSession *)new SpectatorBackend(cb,
                                                 game,
                                                 local_port,
                                                 num_players,
                                                 input_size,
                                                 host_ip,
                                                 host_port);
   return GGPO_OK;
}

GGPOErrorCode
ggpo_client_chat(GGPOSession *ggpo, char *text)
{
   if (!ggpo) {
      return GGPO_ERRORCODE_INVALID_SESSION;
   }
   return ggpo->Chat(text);
}
