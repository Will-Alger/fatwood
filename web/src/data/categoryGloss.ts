/**
 * Plain-English explanations for arXiv categories. The server supplies the
 * official code + name; this adds "what that actually means" without losing
 * anything. Exact code first, then the archive prefix (before the dot), then
 * a generic fallback — cross-listed papers can arrive from any corner of
 * arXiv, so full coverage is impossible and the fallback must read fine.
 *
 * Every category the live corpus serves resolves to a real line, not a
 * fallback: 147 of the 155 have an exact `byCode` entry, and the other 8
 * (`gr-qc`, `hep-th`, `hep-ph`, `hep-ex`, `hep-lat`, `math-ph`, `nucl-th`,
 * `nucl-ex`) are archive-level codes with no dot, so their `byArchive` line is
 * the exact answer rather than a degradation. The fallbacks are for codes that
 * arrive later via cross-listing, not for codes we already serve — an
 * archive-level line ("Physics.") on a category the browse filter lists is a
 * transparency gap, and it also makes that category unfindable by the
 * gloss-text search in CategoryFilter.
 *
 * That invariant is enforced at the bottom of this file against the pinned
 * `LiveCategoryCode` list rather than trusted: it used to hold only by hand,
 * and a new dotted code — or a typo in a key here — would have degraded
 * silently.
 */

import type { LiveCategoryCode } from './liveCategoryCodes'

const byCode = {
  'cs.LG': 'How machines learn from data — training methods, architectures, generalization.',
  'cs.AI': 'Broad artificial intelligence: reasoning, planning, agents, knowledge.',
  'cs.CL': 'Computers understanding and generating human language (NLP, LLMs).',
  'cs.CV': 'Computer vision — machines interpreting images and video.',
  'cs.CR': 'Security and cryptography: attacks, defenses, privacy, encryption.',
  'cs.SE': 'Software engineering: how code gets built, tested, and maintained.',
  'cs.DB': 'Databases and data management systems.',
  'cs.DC': 'Distributed computing: clusters, consensus, parallel systems.',
  'cs.DS': 'Data structures and algorithms — the theory of efficient computation.',
  'cs.IR': 'Information retrieval: search engines, ranking, recommendation.',
  'cs.HC': 'Human–computer interaction: interfaces and how people use them.',
  'cs.NI': 'Computer networks and internet architecture.',
  'cs.OS': 'Operating systems.',
  'cs.PL': 'Programming languages: design, compilers, type systems.',
  'cs.RO': 'Robotics: perception, control, and manipulation in the real world.',
  'cs.SI': 'Social networks and computational social science.',
  'cs.CY': 'Computers and society: policy, ethics, fairness, safety.',
  'cs.GT': 'Game theory in computation: auctions, mechanisms, strategic agents.',
  'cs.MA': 'Multi-agent systems: many AIs interacting or cooperating.',
  'cs.NE': 'Neural and evolutionary computing.',
  'cs.SD': 'Sound and audio processing.',
  'cs.AR': 'Hardware and computer architecture.',
  'cs.CE': 'Computational science applied to engineering and finance problems.',
  'cs.DL': 'Digital libraries: organizing and accessing scholarly content.',
  'cs.IT': 'Information theory: the mathematics of communication and compression.',
  'cs.LO': 'Logic in computer science: formal verification, proofs.',
  'cs.MM': 'Multimedia systems.',
  'cs.PF': 'Performance measurement and modeling of systems.',
  'cs.SC': 'Symbolic computation and computer algebra.',
  'cs.NA': 'Numerical analysis, CS-side alias of math.NA: solvers, accuracy, stability.',
  'cs.SY': 'Systems and control, CS-side alias of eess.SY: feedback, controllers, dynamics.',
  'cs.GR': 'Graphics: rendering, geometry, animation, and simulation for visuals.',
  'cs.CC': 'Computational complexity: which problems are provably hard, and why.',
  'cs.CG': 'Computational geometry: algorithms over points, shapes, and meshes.',
  'cs.DM': 'Discrete mathematics: graphs, combinatorics, and counting arguments.',
  'cs.ET': 'Emerging technologies: computing beyond conventional silicon.',
  'cs.FL': 'Formal languages and automata: grammars, parsers, state machines.',
  'cs.MS': 'Mathematical software: numerical libraries and scientific computing tools.',
  'cs.GL': 'General computer-science literature: surveys and overviews.',
  'cs.OH': 'Computer science that fits no other category.',
  'stat.ML': 'The statistics side of machine learning — theory and methods.',
  'stat.ME': 'Statistical methodology: how to design sound analyses.',
  'stat.AP': 'Statistics applied to real-world domains.',
  'stat.TH': 'Mathematical statistics theory.',
  'stat.CO': 'Statistical computing and simulation methods.',
  'stat.OT': 'Statistics that fits no other category.',
  'q-fin.CP': 'Computational finance: pricing, simulation, and numerical methods.',
  'q-fin.TR': 'Market microstructure and trading: how orders become prices.',
  'q-fin.PM': 'Portfolio management: allocating capital across assets.',
  'q-fin.RM': 'Financial risk management and measurement.',
  'q-fin.ST': 'Statistical analysis of financial markets and returns.',
  'q-fin.MF': 'Mathematical finance: the formal theory behind pricing and hedging.',
  'q-fin.PR': 'Pricing of derivatives and other securities.',
  'q-fin.GN': 'General quantitative finance.',
  'q-fin.EC': 'Economics topics within quantitative finance.',
  'econ.EM': 'Econometrics: statistical methods for economic data.',
  'econ.GN': 'General economics.',
  'econ.TH': 'Economic theory.',
  'math.OC': 'Optimization and control: finding the best decision under constraints.',
  'math.ST': 'Mathematical statistics.',
  'math.PR': 'Probability theory.',
  'math.NA': 'Numerical analysis: making computation accurate and stable.',
  'math.DS': 'Dynamical systems.',
  'math.IT': 'Information theory, math-side alias of cs.IT: coding, capacity, compression.',
  'math.AP': 'Partial differential equations — the analysis behind physical models.',
  'math.CO': 'Combinatorics: counting, graphs, and discrete structures.',
  'math.FA': 'Functional analysis: infinite-dimensional spaces and the operators on them.',
  'math.MP': 'Mathematical physics, the math-side alias of math-ph.',
  'math.SP': 'Spectral theory: eigenvalues and how operators decompose.',
  'math.NT': 'Number theory: primes, integers, and arithmetic structure.',
  'math.LO': 'Mathematical logic: proof theory, model theory, computability.',
  'math.AG': 'Algebraic geometry: the solution sets of polynomial equations.',
  'math.CA': 'Classical analysis and ordinary differential equations.',
  'math.DG': 'Differential geometry: curvature, manifolds, and smooth spaces.',
  'math.AT': 'Algebraic topology: invariants that survive continuous deformation.',
  'math.MG': 'Metric geometry: distance, packing, and discrete geometric structure.',
  'math.CT': 'Category theory: the algebra of structure and composition.',
  'math.RA': 'Rings and algebras: abstract algebraic structures.',
  'math.GR': 'Group theory: the mathematics of symmetry.',
  'math.RT': 'Representation theory: groups and algebras acting on vector spaces.',
  'math.CV': 'Complex analysis: functions of a complex variable.',
  'math.GT': 'Geometric topology: knots, surfaces, and low-dimensional manifolds.',
  'math.AC': 'Commutative algebra: rings, ideals, and polynomial structure.',
  'math.OA': 'Operator algebras: C*-algebras and von Neumann algebras.',
  'math.SG': 'Symplectic geometry: the geometry underlying classical mechanics.',
  'math.GN': 'General topology: continuity, compactness, and point-set structure.',
  'math.QA': 'Quantum algebra: quantum groups and related algebraic structures.',
  'math.KT': 'K-theory and homology: algebraic invariants of spaces and rings.',
  'math.HO': 'History, overviews, and mathematics education.',
  'math.GM': 'Mathematics that fits no other category.',
  'eess.SP': 'Signal processing: extracting information from measurements.',
  'eess.SY': 'Systems and control engineering.',
  'eess.IV': 'Image and video processing.',
  'eess.AS': 'Audio and speech processing.',
  'physics.soc-ph': 'Physics methods applied to social and economic systems.',
  'physics.data-an': 'Data analysis and statistics methods from physics.',
  'physics.comp-ph': 'Computational physics.',
  'physics.flu-dyn': 'Fluid dynamics: flow, turbulence, and CFD simulation.',
  'physics.chem-ph': 'Chemical physics: molecules, reactions, and molecular simulation.',
  'physics.optics': 'Optics: light, lasers, imaging, and photonics.',
  'physics.med-ph': 'Medical physics: imaging, radiation therapy, clinical measurement.',
  'physics.bio-ph': 'Biological physics: physical models of living systems.',
  'physics.ins-det': 'Instruments and detectors: how experiments actually measure things.',
  'physics.app-ph': 'Applied physics: physics aimed at devices and engineering.',
  'physics.ao-ph': 'Atmosphere and ocean: weather, climate, and geophysical fluids.',
  'physics.geo-ph': 'Geophysics: the physics of the Earth — seismic, magnetic, subsurface.',
  'physics.plasm-ph': 'Plasma physics: ionized gases, fusion, and space plasmas.',
  'physics.atom-ph': 'Atomic and molecular physics: spectra, collisions, cold atoms.',
  'physics.class-ph': 'Classical physics: mechanics and electromagnetism, no quantum.',
  'physics.space-ph': 'Space physics: the solar wind, magnetospheres, and near-Earth space.',
  'physics.acc-ph': 'Accelerator physics: particle beams and the machines that steer them.',
  'physics.atm-clus': 'Atomic and molecular clusters: matter between molecules and solids.',
  'physics.ed-ph': 'Physics education: teaching, curriculum, and learning research.',
  'physics.hist-ph': 'History and philosophy of physics.',
  'physics.pop-ph': 'Popular physics: writing aimed at a general audience.',
  'physics.gen-ph': 'Physics that fits no other category.',
  'astro-ph.CO': 'Cosmology: the origin, structure, and fate of the universe.',
  'astro-ph.IM': 'Instrumentation and methods for astronomy.',
  'astro-ph.EP': 'Planets and planetary systems: exoplanets, formation, orbital dynamics.',
  'astro-ph.HE': 'High-energy astrophysics: black holes, neutron stars, cosmic rays.',
  'astro-ph.SR': 'Stars and the Sun: stellar structure, evolution, and activity.',
  'astro-ph.GA': 'Galaxies: their structure, formation, and the Milky Way.',
  'cond-mat.dis-nn': 'Disordered systems and neural networks (statistical physics).',
  'cond-mat.stat-mech': 'Statistical mechanics.',
  'cond-mat.mtrl-sci': 'Materials science: structure, properties, and materials discovery.',
  'cond-mat.soft': 'Soft matter: polymers, colloids, gels, and active matter.',
  'cond-mat.mes-hall': 'Mesoscale and nanoscale physics: devices at the quantum boundary.',
  'cond-mat.str-el': 'Strongly correlated electrons: emergent phases in quantum materials.',
  'cond-mat.supr-con': 'Superconductivity.',
  'cond-mat.quant-gas': 'Ultracold quantum gases: atoms as tunable quantum simulators.',
  'cond-mat.other': 'Condensed matter that fits no other category.',
  'quant-ph': 'Quantum physics and quantum computing.',
  'nlin.AO': 'Adaptation and self-organization in complex systems.',
  'nlin.CD': 'Chaos and nonlinear dynamics: sensitivity, attractors, bifurcations.',
  'nlin.PS': 'Pattern formation and solitons: structure emerging from nonlinearity.',
  'nlin.CG': 'Cellular automata and lattice gases: simple local rules, complex behavior.',
  'nlin.SI': 'Exactly solvable and integrable systems.',
  'q-bio.NC': 'Neurons and cognition: computational neuroscience.',
  'q-bio.QM': 'Quantitative methods in biology.',
  'q-bio.PE': 'Populations and evolution: epidemics, ecology, evolutionary dynamics.',
  'q-bio.BM': 'Biomolecules: proteins and nucleic acids, structure and folding.',
  'q-bio.GN': 'Genomics: sequence data and what it says about biology.',
  'q-bio.MN': 'Molecular networks: gene regulation and metabolic pathway models.',
  'q-bio.TO': 'Tissues and organs: modelling biology above the cellular scale.',
  'q-bio.CB': 'Cell behavior: motility, signaling, and collective cell dynamics.',
  'q-bio.SC': 'Subcellular processes: the molecular machinery inside a cell.',
  'q-bio.OT': 'Quantitative biology that fits no other category.',
}

const byArchive = {
  cs: 'Computer science.',
  stat: 'Statistics.',
  math: 'Mathematics.',
  'q-fin': 'Quantitative finance.',
  econ: 'Economics.',
  eess: 'Electrical engineering and signal processing.',
  physics: 'Physics.',
  'astro-ph': 'Astrophysics — space and the universe.',
  'cond-mat': 'Condensed matter physics.',
  'q-bio': 'Quantitative biology.',
  nlin: 'Nonlinear science and complex systems.',
  'gr-qc': 'General relativity and quantum cosmology.',
  'hep-th': 'Theoretical high-energy physics.',
  'hep-ph': 'High-energy particle physics.',
  'hep-ex': 'Experimental particle physics.',
  'nucl-th': 'Nuclear theory.',
  'nucl-ex': 'Experimental nuclear physics.',
  'hep-lat': 'Lattice QCD — high-energy physics simulated on a computational grid.',
  'math-ph': 'Mathematical physics.',
}

export function categoryGloss(code: string): string {
  const exact = (byCode as Record<string, string>)[code]
  if (exact) return exact
  const archive = (byArchive as Record<string, string>)[code.split('.')[0]]
  if (archive) return archive
  return 'An arXiv research category.'
}

/* ------------------------------------------------------------------ *
 * Coverage checks. These are types, so they cost nothing at runtime and
 * cannot be skipped: `npm run build` runs `tsc -b` first, and a failure
 * here names the offending code in the error message.
 * ------------------------------------------------------------------ */

/**
 * A live code that renders a fallback instead of a real explanation. A dotted
 * code needs its own `byCode` line — falling through to its archive ("Physics.")
 * is exactly the degradation this guards against, so archive coverage does not
 * excuse it. A dotless archive-level code (`hep-th`) is answered exactly by
 * `byArchive`, so either table satisfies it.
 */
type Unglossed<C extends LiveCategoryCode> = C extends keyof typeof byCode
  ? never
  : C extends `${string}.${string}`
    ? C
    : C extends keyof typeof byArchive
      ? never
      : C

type AssertNever<T extends never> = T

/**
 * Every live category renders a real explanation. Fails with "Type 'math.XX'
 * does not satisfy the constraint 'never'" when a code added to
 * `LiveCategoryCode` has no gloss.
 */
export type NoUnglossedLiveCategory = AssertNever<
  { [C in LiveCategoryCode]: Unglossed<C> }[LiveCategoryCode]
>

/**
 * No gloss keyed to a code the corpus does not serve. Catches a typo'd key —
 * which silently degrades that category to its archive line — and flags
 * entries left behind when a category leaves the corpus.
 */
export type NoStaleGloss = AssertNever<Exclude<keyof typeof byCode, LiveCategoryCode>>
