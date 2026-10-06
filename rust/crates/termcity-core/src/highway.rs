#![allow(clippy::cast_possible_truncation, clippy::cast_precision_loss)]

use crate::{GameMap, GameRandom, Pos, RegisteredType};

const EDGE_MARGIN: i32 = 7;
const WINDOW_X: i32 = 3;
const WINDOW_Y: i32 = 2;
const CLEARANCE_X: i32 = 16;
const CLEARANCE_Y: i32 = 8;
const MIN_LEG: i32 = 4;
const MAX_BRIDGE_RUN: i32 = 8;
const MAX_BRIDGE_TOTAL: i32 = 16;
const DIRS: [(i32, i32); 4] = [(0, -1), (1, 0), (0, 1), (-1, 0)];
const NORTH: usize = 0;
const EAST: usize = 1;
const SOUTH: usize = 2;
const WEST: usize = 3;

#[derive(Clone, Debug)]
struct Node {
    x: i32,
    y: i32,
    used: [bool; 4],
}

impl Node {
    fn degree(&self) -> usize {
        self.used.iter().filter(|&&used| used).count()
    }
}

#[derive(Clone, Debug)]
struct Route {
    a: usize,
    port_a: usize,
    b: Option<usize>,
    port_b: usize,
    cells: Vec<Pos>,
    road_id: u8,
    gateway: bool,
}

pub struct HighwayGenerator;

impl HighwayGenerator {
    pub fn generate(map: &mut GameMap, seed: i32) {
        Planner::new(map, seed).run();
    }
}

struct Planner<'a> {
    map: &'a mut GameMap,
    random: GameRandom,
    width: i32,
    height: i32,
    refs: Vec<u8>,
    zones: Vec<i32>,
    nodes: Vec<Node>,
    routes: Vec<Route>,
    highway_id: u8,
    street_id: u8,
}

impl<'a> Planner<'a> {
    fn new(map: &'a mut GameMap, seed: i32) -> Self {
        let highway_id = map
            .content()
            .roads
            .find("Highway")
            .or_else(|| map.content().roads.iter().max_by_key(|road| road.rank))
            .expect("content has a road type")
            .id();
        let street_id = map.content().default_road().id();
        let (width, height) = (map.width(), map.height());
        let count = map.terrain_layer().len();
        Self {
            map,
            random: GameRandom::for_stage(seed, "highways"),
            width,
            height,
            refs: vec![0; count],
            zones: vec![-1; count],
            nodes: Vec::new(),
            routes: Vec::new(),
            highway_id,
            street_id,
        }
    }

    fn run(&mut self) {
        for _ in 0..10 {
            self.reset();
            if self.try_build() {
                self.add_street_stubs();
                self.rasterize();
                return;
            }
        }
        self.reset();
        self.fallback();
    }

    fn reset(&mut self) {
        self.refs.fill(0);
        self.zones.fill(-1);
        self.nodes.clear();
        self.routes.clear();
    }

    fn try_build(&mut self) -> bool {
        self.place_interchanges();
        if self.nodes.len() < 2 {
            return false;
        }
        self.grow_tree();
        self.add_loops();
        self.fix_dead_ends();
        self.add_gateways();
        self.routes.iter().any(|route| route.gateway)
            && self.routes.iter().map(|route| route.cells.len()).sum::<usize>()
                >= usize::try_from((self.width + self.height) / 2).unwrap_or_default()
    }

    fn index(&self, x: i32, y: i32) -> usize {
        usize::try_from(y * self.width + x).expect("validated coordinate")
    }

    fn open(&self, x: i32, y: i32) -> bool {
        self.map.terrain_at(x, y).buildable
    }

    fn boundary(&self, x: i32, y: i32) -> bool {
        x == 0 || y == 0 || x == self.width - 1 || y == self.height - 1
    }

    fn dist2(&self, a: usize, b: usize) -> f64 {
        let dx = f64::from(self.nodes[a].x - self.nodes[b].x);
        let dy = 2.0 * f64::from(self.nodes[a].y - self.nodes[b].y);
        dx.mul_add(dx, dy * dy)
    }

    fn place_interchanges(&mut self) {
        if self.width <= 2 * EDGE_MARGIN || self.height <= 2 * (EDGE_MARGIN - 2) {
            return;
        }
        let area = f64::from(self.width) * f64::from(self.height);
        let target =
            usize::try_from((area / 6000.0).round() as i32).expect("positive map area").max(3);
        let min_dist2 = 0.55 * 0.55 * 2.0 * area / target as f64;
        for _ in 0..target * 40 {
            if self.nodes.len() == target {
                break;
            }
            let x = self.random.next_range(EDGE_MARGIN, self.width - EDGE_MARGIN);
            let y = self.random.next_range(EDGE_MARGIN - 2, self.height - (EDGE_MARGIN - 2));
            if !self.window_is_open(x, y) {
                continue;
            }
            let candidate = Node { x, y, used: [false; 4] };
            let too_close = self.nodes.iter().any(|node| {
                let dx = f64::from(node.x - x);
                let dy = 2.0 * f64::from(node.y - y);
                dx.mul_add(dx, dy * dy) < min_dist2
                    || ((node.x - x).abs() <= 2 * CLEARANCE_X
                        && (node.y - y).abs() <= 2 * CLEARANCE_Y)
            });
            if too_close {
                continue;
            }
            let id = self.nodes.len();
            self.nodes.push(candidate);
            for dy in -CLEARANCE_Y..=CLEARANCE_Y {
                for dx in -CLEARANCE_X..=CLEARANCE_X {
                    let (zx, zy) = (x + dx, y + dy);
                    if self.map.in_bounds(zx, zy) {
                        let index = self.index(zx, zy);
                        self.zones[index] = i32::try_from(id).expect("node count is bounded");
                    }
                }
            }
        }
    }

    fn window_is_open(&self, x: i32, y: i32) -> bool {
        (-WINDOW_Y..=WINDOW_Y).all(|dy| (-WINDOW_X..=WINDOW_X).all(|dx| self.open(x + dx, y + dy)))
    }

    fn grow_tree(&mut self) {
        let node_count = i32::try_from(self.nodes.len()).expect("node count is bounded");
        let start =
            usize::try_from(self.random.next(node_count)).expect("random node is non-negative");
        let mut connected = vec![start];
        let mut remaining: Vec<_> = (0..self.nodes.len()).filter(|&node| node != start).collect();
        while !remaining.is_empty() {
            let pick = (0..remaining.len())
                .min_by(|&left, &right| {
                    let left_distance = connected
                        .iter()
                        .map(|&node| self.dist2(remaining[left], node))
                        .fold(f64::INFINITY, f64::min);
                    let right_distance = connected
                        .iter()
                        .map(|&node| self.dist2(remaining[right], node))
                        .fold(f64::INFINITY, f64::min);
                    left_distance.total_cmp(&right_distance)
                })
                .expect("remaining is not empty");
            let next = remaining.swap_remove(pick);
            let mut nearest = connected.clone();
            nearest.sort_by(|&left, &right| {
                self.dist2(left, next).total_cmp(&self.dist2(right, next)).then(left.cmp(&right))
            });
            let linked = nearest
                .into_iter()
                .take(5)
                .find_map(|other| self.best_route(other, next))
                .is_some_and(|route| {
                    self.commit(route);
                    true
                });
            if linked {
                connected.push(next);
            }
        }
    }

    fn add_loops(&mut self) {
        let mut nodes: Vec<_> = (0..self.nodes.len())
            .filter(|&node| self.nodes[node].degree() > 0)
            .map(|node| (self.random.next_u64(), node))
            .collect();
        nodes.sort_unstable();
        for (_, a) in nodes {
            if !self.random.chance(0.2) {
                continue;
            }
            let mut candidates: Vec<_> = (0..self.nodes.len())
                .filter(|&b| b != a && self.nodes[b].degree() > 0 && !self.linked(a, b))
                .collect();
            candidates.sort_by(|&left, &right| {
                self.dist2(a, left).total_cmp(&self.dist2(a, right)).then(left.cmp(&right))
            });
            if let Some(route) = candidates.into_iter().take(3).find_map(|b| self.best_route(a, b))
            {
                self.commit(route);
            }
        }
    }

    fn linked(&self, a: usize, b: usize) -> bool {
        self.routes.iter().any(|route| {
            (route.a == a && route.b == Some(b)) || (route.a == b && route.b == Some(a))
        })
    }

    fn fix_dead_ends(&mut self) {
        while let Some(node) = (0..self.nodes.len()).find(|&node| self.nodes[node].degree() == 1) {
            if self.try_gateway(node, 70) || self.try_link_onward(node) {
                continue;
            }
            let route = self
                .routes
                .iter()
                .position(|route| route.a == node || route.b == Some(node))
                .expect("degree-one node has a route");
            self.remove(route);
        }
    }

    fn try_link_onward(&mut self, node: usize) -> bool {
        let mut candidates: Vec<_> = (0..self.nodes.len())
            .filter(|&other| {
                other != node && self.nodes[other].degree() > 0 && !self.linked(node, other)
            })
            .collect();
        candidates.sort_by(|&left, &right| {
            self.dist2(node, left).total_cmp(&self.dist2(node, right)).then(left.cmp(&right))
        });
        if let Some(route) =
            candidates.into_iter().take(4).find_map(|other| self.best_route(node, other))
        {
            self.commit(route);
            true
        } else {
            false
        }
    }

    fn try_gateway(&mut self, node: usize, max_length: usize) -> bool {
        let route =
            (0..4).filter_map(|port| self.ray(node, port)).min_by_key(|route| route.cells.len());
        if let Some(route) = route.filter(|route| route.cells.len() <= max_length) {
            self.commit(route);
            true
        } else {
            false
        }
    }

    fn ray(&self, node: usize, port: usize) -> Option<Route> {
        if self.nodes[node].used[port] {
            return None;
        }
        let (dx, dy) = DIRS[port];
        let mut cells = vec![Pos::new(self.nodes[node].x, self.nodes[node].y)];
        let (mut x, mut y) = (self.nodes[node].x, self.nodes[node].y);
        loop {
            x += dx;
            y += dy;
            if !self.map.in_bounds(x, y) {
                return None;
            }
            cells.push(Pos::new(x, y));
            if self.boundary(x, y) {
                break;
            }
        }
        self.valid(&cells, &[], node, None, MAX_BRIDGE_TOTAL, MAX_BRIDGE_RUN).then_some(Route {
            a: node,
            port_a: port,
            b: None,
            port_b: 0,
            cells,
            road_id: self.highway_id,
            gateway: true,
        })
    }

    fn add_gateways(&mut self) {
        let wanted =
            usize::try_from((2.0 * f64::from(self.width + self.height) / 220.0).round() as i32)
                .expect("map dimensions are positive")
                .max(2);
        let mut ends: Vec<_> = self
            .routes
            .iter()
            .filter(|route| route.gateway)
            .filter_map(|route| route.cells.last().copied())
            .collect();
        let mut candidates = Vec::new();
        for node in 0..self.nodes.len() {
            if self.nodes[node].degree() == 0 {
                continue;
            }
            candidates.extend((0..4).filter_map(|port| self.ray(node, port)));
        }
        candidates.sort_by_key(|route| (route.cells.len(), route.a, route.port_a));
        for route in candidates {
            if ends.len() >= wanted {
                break;
            }
            let end = *route.cells.last().expect("gateway has cells");
            if self.nodes[route.a].used[route.port_a]
                || ends
                    .iter()
                    .any(|other| (other.x - end.x).abs().max((other.y - end.y).abs()) < 24)
                || !self.valid(&route.cells, &[], route.a, None, MAX_BRIDGE_TOTAL, MAX_BRIDGE_RUN)
            {
                continue;
            }
            self.commit(route);
            ends.push(end);
        }
    }

    fn add_street_stubs(&mut self) {
        let nodes: Vec<_> =
            (0..self.nodes.len()).filter(|&node| self.nodes[node].degree() > 0).collect();
        let mut total = 0;
        for pass in 0..2 {
            if total > 0 {
                break;
            }
            for &node in &nodes {
                let mut ports = [
                    (self.random.next_u64(), 0),
                    (self.random.next_u64(), 1),
                    (self.random.next_u64(), 2),
                    (self.random.next_u64(), 3),
                ];
                ports.sort_unstable();
                let mut placed = 0;
                for (_, port) in ports {
                    let chance = if pass == 1 {
                        1.0
                    } else if placed == 0 {
                        0.6
                    } else {
                        0.2
                    };
                    if self.nodes[node].used[port] || !self.random.chance(chance) {
                        continue;
                    }
                    let length = self.random.next_range(6, 13);
                    let (dx, dy) = DIRS[port];
                    let cells: Vec<_> = (0..=length)
                        .map(|step| {
                            Pos::new(self.nodes[node].x + dx * step, self.nodes[node].y + dy * step)
                        })
                        .collect();
                    let last = *cells.last().expect("stub has cells");
                    if !self.map.contains(last)
                        || self.boundary(last.x, last.y)
                        || !self.valid(&cells, &[], node, None, 0, 0)
                    {
                        continue;
                    }
                    self.commit(Route {
                        a: node,
                        port_a: port,
                        b: None,
                        port_b: 0,
                        cells,
                        road_id: self.street_id,
                        gateway: false,
                    });
                    total += 1;
                    placed += 1;
                    if placed == 2 || pass == 1 {
                        break;
                    }
                }
                if pass == 1 && total > 0 {
                    break;
                }
            }
        }
    }

    fn best_route(&mut self, a: usize, b: usize) -> Option<Route> {
        let orthogonal_only = self.random.chance(0.3);
        let mut best: Option<(i32, Route)> = None;
        for (corners, port_a, port_b) in self.candidates(a, b) {
            if self.nodes[a].used[port_a] || self.nodes[b].used[port_b] {
                continue;
            }
            let diagonal =
                corners.windows(2).any(|pair| pair[0].x != pair[1].x && pair[0].y != pair[1].y);
            if diagonal && orthogonal_only {
                continue;
            }
            let cells = expand(&corners);
            let bends = &corners[1..corners.len() - 1];
            if !self.valid(&cells, bends, a, Some(b), MAX_BRIDGE_TOTAL, MAX_BRIDGE_RUN) {
                continue;
            }
            let water =
                i32::try_from(cells.iter().filter(|cell| !self.open(cell.x, cell.y)).count())
                    .expect("route length fits in i32");
            if diagonal && water > 0 {
                continue;
            }
            let length = i32::try_from(cells.len()).expect("route length fits in i32");
            let score = length + 6 * water + bend_cost(&corners) - i32::from(diagonal) * 3;
            if best.as_ref().is_none_or(|(best_score, _)| score < *best_score) {
                best = Some((
                    score,
                    Route {
                        a,
                        port_a,
                        b: Some(b),
                        port_b,
                        cells,
                        road_id: self.highway_id,
                        gateway: false,
                    },
                ));
            }
        }
        best.map(|(_, route)| route)
    }

    fn candidates(&self, a: usize, b: usize) -> Vec<(Vec<Pos>, usize, usize)> {
        let (a, b) = (&self.nodes[a], &self.nodes[b]);
        let (dx, dy) = (b.x - a.x, b.y - a.y);
        let (sx, sy) = (dx.signum(), dy.signum());
        let horizontal = if sx > 0 { EAST } else { WEST };
        let vertical = if sy > 0 { SOUTH } else { NORTH };
        if dx == 0 {
            return vec![(
                vec![Pos::new(a.x, a.y), Pos::new(b.x, b.y)],
                vertical,
                (vertical + 2) % 4,
            )];
        }
        if dy == 0 {
            return vec![(
                vec![Pos::new(a.x, a.y), Pos::new(b.x, b.y)],
                horizontal,
                (horizontal + 2) % 4,
            )];
        }
        let (ax, ay) = (dx.abs(), dy.abs());
        let mut result = Vec::new();
        if ax >= MIN_LEG && ay >= MIN_LEG {
            result.push((
                vec![Pos::new(a.x, a.y), Pos::new(b.x, a.y), Pos::new(b.x, b.y)],
                horizontal,
                if sy > 0 { NORTH } else { SOUTH },
            ));
            result.push((
                vec![Pos::new(a.x, a.y), Pos::new(a.x, b.y), Pos::new(b.x, b.y)],
                vertical,
                if sx > 0 { WEST } else { EAST },
            ));
        }
        for k in [2, 3, 5] {
            if ax - k >= MIN_LEG && ay - k >= MIN_LEG {
                result.push((
                    vec![
                        Pos::new(a.x, a.y),
                        Pos::new(b.x - sx * k, a.y),
                        Pos::new(b.x, a.y + sy * k),
                        Pos::new(b.x, b.y),
                    ],
                    horizontal,
                    if sy > 0 { NORTH } else { SOUTH },
                ));
                result.push((
                    vec![
                        Pos::new(a.x, a.y),
                        Pos::new(a.x, b.y - sy * k),
                        Pos::new(a.x + sx * k, b.y),
                        Pos::new(b.x, b.y),
                    ],
                    vertical,
                    if sx > 0 { WEST } else { EAST },
                ));
            }
        }
        if ax >= 2 * MIN_LEG && ay >= 3 {
            for fraction in [0.3, 0.5, 0.7] {
                let mx = a.x + (f64::from(dx) * fraction).round() as i32;
                result.push((
                    vec![
                        Pos::new(a.x, a.y),
                        Pos::new(mx, a.y),
                        Pos::new(mx, b.y),
                        Pos::new(b.x, b.y),
                    ],
                    horizontal,
                    (horizontal + 2) % 4,
                ));
            }
        }
        if ay >= 2 * MIN_LEG && ax >= 3 {
            for fraction in [0.3, 0.5, 0.7] {
                let my = a.y + (f64::from(dy) * fraction).round() as i32;
                result.push((
                    vec![
                        Pos::new(a.x, a.y),
                        Pos::new(a.x, my),
                        Pos::new(b.x, my),
                        Pos::new(b.x, b.y),
                    ],
                    vertical,
                    (vertical + 2) % 4,
                ));
            }
        }
        result
    }

    fn valid(
        &self,
        cells: &[Pos],
        bends: &[Pos],
        a: usize,
        b: Option<usize>,
        max_water: i32,
        max_run: i32,
    ) -> bool {
        let mut water = 0;
        let mut run = 0;
        for cell in cells {
            if !self.map.contains(*cell) {
                return false;
            }
            if self.open(cell.x, cell.y) {
                run = 0;
            } else {
                water += 1;
                run += 1;
                if water > max_water || run > max_run {
                    return false;
                }
            }
            let zone = self.zones[self.index(cell.x, cell.y)];
            if zone >= 0 && usize::try_from(zone).is_ok_and(|zone| zone != a && Some(zone) != b) {
                return false;
            }
            if !self.in_window(*cell, a)
                && b.is_none_or(|node| !self.in_window(*cell, node))
                && self.crowded(cell.x, cell.y)
            {
                return false;
            }
        }
        bends.iter().all(|bend| self.open(bend.x, bend.y))
            && cells.last().is_some_and(|last| self.open(last.x, last.y))
    }

    fn in_window(&self, cell: Pos, node: usize) -> bool {
        (cell.x - self.nodes[node].x).abs() <= CLEARANCE_X
            && (cell.y - self.nodes[node].y).abs() <= CLEARANCE_Y
    }

    fn crowded(&self, x: i32, y: i32) -> bool {
        (-CLEARANCE_Y..=CLEARANCE_Y).any(|dy| {
            (-CLEARANCE_X..=CLEARANCE_X).any(|dx| {
                self.map.in_bounds(x + dx, y + dy) && self.refs[self.index(x + dx, y + dy)] > 0
            })
        })
    }

    fn commit(&mut self, route: Route) {
        for cell in route.cells.iter().skip(1) {
            let index = self.index(cell.x, cell.y);
            self.refs[index] = self.refs[index].saturating_add(1);
        }
        self.nodes[route.a].used[route.port_a] = true;
        if let Some(b) = route.b {
            self.nodes[b].used[route.port_b] = true;
        }
        self.routes.push(route);
    }

    fn remove(&mut self, route_index: usize) {
        let route = self.routes.swap_remove(route_index);
        for cell in route.cells.iter().skip(1) {
            let index = self.index(cell.x, cell.y);
            self.refs[index] -= 1;
        }
        self.nodes[route.a].used[route.port_a] = false;
        if let Some(b) = route.b {
            self.nodes[b].used[route.port_b] = false;
        }
    }

    fn rasterize(&mut self) {
        let mut routes = self.routes.clone();
        routes.sort_by_key(|route| i32::from(route.road_id == self.highway_id));
        for route in routes {
            for cell in route.cells {
                self.map.set_road(cell.x, cell.y, route.road_id);
            }
        }
    }

    fn fallback(&mut self) {
        for horizontal in [true, false] {
            let (length, across) =
                if horizontal { (self.width, self.height) } else { (self.height, self.width) };
            let mut best = Vec::new();
            let mut best_water = i32::MAX;
            for _ in 0..24 {
                let lane = (f64::from(across) * (0.25 + 0.5 * self.random.next_f64())) as i32;
                let path: Vec<_> = (0..length)
                    .map(
                        |step| {
                            if horizontal { Pos::new(step, lane) } else { Pos::new(lane, step) }
                        },
                    )
                    .collect();
                let water =
                    i32::try_from(path.iter().filter(|cell| !self.open(cell.x, cell.y)).count())
                        .expect("map dimension fits in i32");
                if water < best_water {
                    best = path;
                    best_water = water;
                }
            }
            for cell in best {
                self.map.set_road(cell.x, cell.y, self.highway_id);
            }
        }
    }
}

fn expand(corners: &[Pos]) -> Vec<Pos> {
    let mut cells = vec![corners[0]];
    for pair in corners.windows(2) {
        let (sx, sy) = ((pair[1].x - pair[0].x).signum(), (pair[1].y - pair[0].y).signum());
        let diagonal = sx != 0 && sy != 0;
        let mut position = pair[0];
        while position != pair[1] {
            if diagonal {
                position.x += sx;
                cells.push(position);
                position.y += sy;
            } else {
                position = position.offset(sx, sy);
            }
            cells.push(position);
        }
    }
    cells
}

fn bend_cost(corners: &[Pos]) -> i32 {
    corners
        .windows(3)
        .map(|points| {
            let (ax, ay) =
                ((points[1].x - points[0].x).signum(), (points[1].y - points[0].y).signum());
            let (bx, by) =
                ((points[2].x - points[1].x).signum(), (points[2].y - points[1].y).signum());
            if ax * bx + ay * by == 0 { 8 } else { 4 }
        })
        .sum()
}
