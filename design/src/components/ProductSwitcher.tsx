export interface Product {
  id: string;
  label: string;
  href: string;
}

export interface ProductSwitcherProps {
  products: Product[];
  currentId: string;
}

/**
 * Ürünler ayrı SPA build'leri olduğundan geçiş her zaman tam sayfa navigasyonudur —
 * react-router Link değil düz <a href>.
 */
export function ProductSwitcher({ products, currentId }: ProductSwitcherProps) {
  return (
    <nav className="alp-product-switcher" aria-label="Ürünler">
      <ul>
        {products.map((product) => (
          <li key={product.id}>
            <a
              href={product.href}
              aria-current={product.id === currentId ? 'page' : undefined}
              className={product.id === currentId ? 'alp-product-switcher__item alp-product-switcher__item--current' : 'alp-product-switcher__item'}
            >
              {product.label}
            </a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
