# LineTester Network Testing

LineTester coordinates active network probes among registered datacenter nodes while preserving each node's local decision to participate.

## Language

**Global test session**:
The registry-center-owned run state that authorizes ready nodes to send probes. Only the center node can start or stop it.
_Avoid_: Global ready, center online

**Ready node**:
A node whose operator has locally selected “Start test” and which is prepared to participate. A ready node may be distributed as a probe target; readiness alone neither authorizes sending probes nor opens the data-plane listener while the global session is stopped.
_Avoid_: Online node, registered node, active link

**Eligible probe target**:
A ready, online, distributable node whose advertised endpoints are included in the registry snapshot for other nodes.
_Avoid_: Registered node, visible node

**External endpoint**:
A public IP address and mapped port pair that other nodes use to reach one test node. A node may advertise several external endpoints, each with its own port.
_Avoid_: Public IP list, shared node port

**Registered node**:
A node known to the registry center. Registration alone does not mean the node is ready to receive probes or allowed to send them.
_Avoid_: Ready node, running node
