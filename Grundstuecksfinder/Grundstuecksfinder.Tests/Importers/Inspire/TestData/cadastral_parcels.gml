<?xml version="1.0" encoding="UTF-8"?>
<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"
                        xmlns:gml="http://www.opengis.net/gml/3.2"
                        xmlns="http://inspire.ec.europa.eu/schemas/cp/4.0"
                        numberMatched="3" numberReturned="3">
  <wfs:member>
    <CadastralParcel gml:id="CP_1">
      <areaValue uom="m2">1250.5</areaValue>
      <geometry>
        <gml:Polygon gml:id="CP_1_geom" srsName="http://www.opengis.net/def/crs/epsg/0/25832" srsDimension="2">
          <gml:exterior>
            <gml:LinearRing>
              <gml:posList>560000 5990000 560100 5990000 560100 5990100 560000 5990100 560000 5990000</gml:posList>
            </gml:LinearRing>
          </gml:exterior>
        </gml:Polygon>
      </geometry>
    </CadastralParcel>
  </wfs:member>
  <wfs:member>
    <CadastralParcel gml:id="CP_2">
      <areaValue uom="m2">840.0</areaValue>
      <geometry>
        <gml:Polygon gml:id="CP_2_geom" srsName="http://www.opengis.net/def/crs/epsg/0/25832" srsDimension="2">
          <gml:exterior>
            <gml:LinearRing>
              <gml:posList>560200 5990000 560300 5990000 560300 5990100 560200 5990100 560200 5990000</gml:posList>
            </gml:LinearRing>
          </gml:exterior>
        </gml:Polygon>
      </geometry>
    </CadastralParcel>
  </wfs:member>
  <wfs:member>
    <CadastralParcel gml:id="CP_3_malformed">
      <!-- No areaValue: parser must skip this feature, not throw. -->
      <geometry>
        <gml:Polygon gml:id="CP_3_geom" srsName="http://www.opengis.net/def/crs/epsg/0/25832" srsDimension="2">
          <gml:exterior>
            <gml:LinearRing>
              <gml:posList>561000 5991000 561100 5991000 561100 5991100 561000 5991100 561000 5991000</gml:posList>
            </gml:LinearRing>
          </gml:exterior>
        </gml:Polygon>
      </geometry>
    </CadastralParcel>
  </wfs:member>
</wfs:FeatureCollection>
