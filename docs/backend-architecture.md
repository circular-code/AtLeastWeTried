## Constraints

- Use official Flattiverse C# Connector
- have a websocket interface to the web gui documented in gateway-webui-communication

## Internal Components

- Core Processing Loop for each Game Connection to the Flattiverse Connector

- Individual services that can be instanced across connections
  different services handle, each service is responsible for its own state.
  - manuvering
  - scanning
  - mapping (state management for different objects of the map)
  - path finding

- Communication: Websockt transport that handles different clients, and handles the communication with the webui