export function isolatedTreemonEnvironment(
  inherited,
  {
    apiPort,
    configDirectory,
    terminalStateDirectory,
  },
) {
  const isolatedPort = String(apiPort);
  return {
    ...inherited,
    TREEMON_PORT: isolatedPort,
    TREEMON_PORTS: isolatedPort,
    TREEMON_CONFIG_DIR: configDirectory,
    TREEMON_TERMINAL_STATE_DIR: terminalStateDirectory,
  };
}
