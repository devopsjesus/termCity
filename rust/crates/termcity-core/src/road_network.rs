use std::collections::VecDeque;

use crate::GameMap;

pub const ROAD_NEIGHBOURS: [(i32, i32); 4] = [(0, -1), (1, 0), (0, 1), (-1, 0)];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RoadNetwork {
    connected: Vec<bool>,
    served: Vec<bool>,
    connected_road_count: usize,
}

impl RoadNetwork {
    #[must_use]
    pub fn compute(map: &GameMap, service_reach: i32) -> Self {
        let mut connected = vec![false; map.road_layer().len()];
        let mut served = vec![false; map.road_layer().len()];
        let mut roads = VecDeque::new();

        for index in map.road_cells() {
            let position = map.position_of(index);
            if map.is_edge(position.x, position.y) {
                connected[index] = true;
                roads.push_back(index);
            }
        }

        let mut connected_cells = Vec::with_capacity(map.road_count());
        while let Some(index) = roads.pop_front() {
            connected_cells.push(index);
            let position = map.position_of(index);
            for (dx, dy) in ROAD_NEIGHBOURS {
                let (x, y) = (position.x + dx, position.y + dy);
                if map.has_road(x, y) {
                    let neighbour = map.index(x, y);
                    if !connected[neighbour] {
                        connected[neighbour] = true;
                        roads.push_back(neighbour);
                    }
                }
            }
        }

        let mut frontier = VecDeque::from(connected_cells.clone());
        for &index in &connected_cells {
            served[index] = true;
        }

        for _ in 0..service_reach.max(0) {
            let count = frontier.len();
            if count == 0 {
                break;
            }
            for _ in 0..count {
                let Some(index) = frontier.pop_front() else {
                    break;
                };
                let position = map.position_of(index);
                for (dx, dy) in ROAD_NEIGHBOURS {
                    let (x, y) = (position.x + dx, position.y + dy);
                    if !map.in_bounds(x, y) {
                        continue;
                    }
                    let neighbour = map.index(x, y);
                    if !served[neighbour] && map.terrain_at(x, y).buildable {
                        served[neighbour] = true;
                        frontier.push_back(neighbour);
                    }
                }
            }
        }

        Self { connected, served, connected_road_count: connected_cells.len() }
    }

    #[must_use]
    pub const fn connected_road_count(&self) -> usize {
        self.connected_road_count
    }

    #[must_use]
    pub fn is_connected(&self, map: &GameMap, x: i32, y: i32) -> bool {
        map.in_bounds(x, y) && self.connected[map.index(x, y)]
    }

    #[must_use]
    pub fn is_served(&self, map: &GameMap, x: i32, y: i32) -> bool {
        map.in_bounds(x, y) && self.served[map.index(x, y)]
    }

    #[must_use]
    pub fn is_index_served(&self, index: usize) -> bool {
        self.served[index]
    }
}
